namespace AgentLang

open System
open System.Security.Cryptography
open System.Text

/// Lowering for the first explicit Flow syntax slice. Resolution is static and
/// the output remains ordinary Expr consumed by the existing checked compiler.
module FlowLowering =
    type Context =
        { CompilerContext: Compiler.IrLoweringContext
          /// Audited parameter names for words whose names are not carried by the
          /// legacy WordDefinition. Record constructor names derive from fields.
          ParameterNames: Map<string, string list>
          /// Transient private source markers already lowered into words in this context.
          SourceOrigins: Map<SourceSpan, SourceSpan> }

    type CompiledExpression =
        { Lowered: FlowLoweredExpression
          Program: VerifiedIrProgram
          Body: VerifiedIrBody
          SiteOrigins: Map<SourceSiteId, SourceSpan> }

    type CompiledWord =
        { Lowered: FlowLoweredWord
          Context: Context
          Program: VerifiedIrProgram
          SiteOrigins: Map<SourceSiteId, SourceSpan> }

    type CompiledTest =
        { Lowered: FlowLoweredTest
          Program: VerifiedIrProgram
          Body: VerifiedIrBody
          ExpectationBody: VerifiedIrBody option
          BodySiteOrigins: Map<SourceSiteId, SourceSpan>
          ExpectationSiteOrigins: Map<SourceSiteId, SourceSpan> option }

    type CompiledExample =
        { Lowered: FlowLoweredExample
          Program: VerifiedIrProgram
          Body: VerifiedIrBody
          SiteOrigins: Map<SourceSiteId, SourceSpan> }

    /// Host-supplied revision intent for one word in an atomic Flow batch.
    /// Additions must use a previously unused stable ID and a nonnegative revision.
    /// Replacements preserve the existing ID and entry metadata while advancing
    /// the host-owned revision.
    [<RequireQualifiedAccess>]
    type FlowWordRevisionIntent =
        | Add of WordId * revision: int
        | Replace of WordId * expectedRevision: int * revision: int

    type FlowWordChange =
        { Definition: FlowWordDefinition
          RevisionIntent: FlowWordRevisionIntent }

    type FlowBatchCompilation =
        { LoweredWords: FlowLoweredWord list
          Context: Context
          Program: VerifiedIrProgram
          SiteOrigins: Map<SourceSiteId, SourceSpan> }

    /// Exact source bytes plus the host's owner assertion. The lowering API
    /// checks the hash and compiler snapshot identity; the host remains
    /// responsible for establishing manifest membership.
    type FlowSourceDocument =
        { OwnerName: string
          OwnerId: WordId
          OwnerRevision: int
          Reference: SourceRef
          SourceFile: string
          Content: string }

    /// The owner set is explicitly declared by the host because a legacy
    /// compiler Context does not say which definitions were authored in Flow.
    type FlowSourceInventory =
        { ExpectedFlowOwnerIds: Set<WordId>
          Sources: FlowSourceDocument list }

    type FlowSourceChange =
        { RevisionIntent: FlowWordRevisionIntent
          Source: FlowSourceDocument }

    [<RequireQualifiedAccess>]
    type FlowCallForm =
        | Direct
        | AbsoluteRoot
        | DotStage of string
        | StaticCallback of string * FlowWordReferenceQualification

    [<RequireQualifiedAccess>]
    type FlowCallTargetIdentity =
        | UserWord of WordId
        | Primitive of PrimitiveId
        | GeneratedWord of WordId

    type FlowCallSite =
        { Path: FlowAstPath
          Span: SourceSpan
          Form: FlowCallForm
          RequestedName: string
          Target: FlowCallTargetIdentity
          TargetRevision: int option }

    /// A transient binding proof, scoped to exact owner source bytes and a
    /// structural AST path rather than source-span uniqueness or IR ordinals.
    type FlowCallBinding =
        { OwnerName: string
          OwnerId: WordId
          OwnerRevision: int
          Source: SourceRef
          Site: FlowCallSite }

    type FlowBoundBatchCompilation =
        { LoweredWords: FlowLoweredWord list
          Context: Context
          Program: VerifiedIrProgram
          SiteOrigins: Map<SourceSiteId, SourceSpan>
          CallBindings: FlowCallBinding list }

    [<RequireQualifiedAccess>]
    type FlowAttachmentKind =
        | Test
        | Example

    /// Stable identity for one source-backed test or example attached to a word.
    type FlowAttachmentKey =
        { OwnerId: WordId
          Kind: FlowAttachmentKind
          CaseName: string }

    /// Exact host-inventoried bytes for one Flow test or example.
    type FlowAttachmentSourceDocument =
        { OwnerName: string
          OwnerId: WordId
          OwnerRevision: int
          Kind: FlowAttachmentKind
          CaseName: string
          Reference: SourceRef
          SourceFile: string
          Content: string }

    type FlowAttachmentInventory =
        { /// Host-authenticated expected key-to-object mapping. The compiler only
          /// proves that supplied documents match this declaration and the given
          /// Context; the host establishes durable manifest membership.
          ExpectedSources: Map<FlowAttachmentKey, SourceRef>
          Sources: FlowAttachmentSourceDocument list }

    [<RequireQualifiedAccess>]
    type FlowAttachmentChange =
        | Add of FlowAttachmentSourceDocument
        | Replace of expectedPriorReference: SourceRef * FlowAttachmentSourceDocument
        | Remove of key: FlowAttachmentKey * expectedPriorReference: SourceRef

    [<RequireQualifiedAccess>]
    type FlowAttachmentBodyRole =
        | Actual
        | ExpectedExpression

    type FlowAttachmentCallBinding =
        { Attachment: FlowAttachmentKey
          OwnerName: string
          OwnerRevision: int
          Source: SourceRef
          BodyRole: FlowAttachmentBodyRole
          Site: FlowCallSite }

    [<RequireQualifiedAccess>]
    type FlowCompiledAttachment =
        | Test of source: FlowAttachmentSourceDocument * compiled: CompiledTest
        | Example of source: FlowAttachmentSourceDocument * compiled: CompiledExample

    type FlowBoundProjectCompilation =
        { WordCompilation: FlowBoundBatchCompilation
          Attachments: FlowCompiledAttachment list
          AttachmentBindings: FlowAttachmentCallBinding list }

    /// Structural call sites from a host-built attachment AST. Like the
    /// standalone word helper, this does not authenticate source bytes or
    /// establish project-manifest membership.
    type CompiledCallBoundTest =
        { Compiled: CompiledTest
          CallSites: Map<FlowAttachmentBodyRole, FlowCallSite list> }

    type CompiledCallBoundWord =
        { Lowered: FlowLoweredWord
          Context: Context
          Program: VerifiedIrProgram
          SiteOrigins: Map<SourceSiteId, SourceSpan>
          CallSites: FlowCallSite list }

    type private Binding =
        { InternalName: string
          Type: LangType }

    [<RequireQualifiedAccess>]
    type private SignatureKind =
        | TrustedPrimitive of string
        | Generated of Builtin
        | UserWord

    type private Candidate =
        { Name: string
          Inputs: LangType list
          Outputs: LangType list
          Effects: Set<string>
          Kind: SignatureKind
          ParameterNames: string list option
          Identity: WordId option
          Revision: int
          Span: SourceSpan }

    type private FlowCallEvent =
        { Path: FlowAstPath
          Span: SourceSpan
          Form: FlowCallForm
          RequestedName: string
          Candidate: Candidate }

    type private LoweredFragment =
        { Expressions: Expr list
          CallEvents: FlowCallEvent list }

    type private SemanticExpr =
        | SemanticPush of Literal
        | SemanticCall of string
        | SemanticConstruct of ContainerConstructor * LangType list
        | SemanticMapList of string
        | SemanticFilterList of string
        | SemanticEachList of string
        | SemanticLet of string
        | SemanticLoad of string
        | SemanticIf of SemanticExpr list * SemanticExpr list
        | SemanticScope of SemanticExpr list
        | SemanticMatchOption of string * SemanticExpr list * SemanticExpr list
        | SemanticMatchResult of string * string * SemanticExpr list * SemanticExpr list

    type private RetainedFlowSource =
        { Document: FlowSourceDocument
          Definition: FlowWordDefinition
          BaseBindings: FlowCallSite list }

    type private AuthoredLoweredOwner =
        { Document: FlowSourceDocument option
          OwnerName: string
          OwnerId: WordId
          OwnerRevision: int
          Lowered: FlowLoweredWord
          CallEvents: FlowCallEvent list }

    type private BatchCompilationArtifacts =
        { Compilation: FlowBatchCompilation
          AuthoredOwners: AuthoredLoweredOwner list }

    type private ValidatedFlowSourceChange =
        { Change: FlowWordChange
          Source: FlowSourceDocument }

    type private ParsedFlowAttachment =
        | ParsedFlowTest of FlowTestDefinition
        | ParsedFlowExample of FlowExampleDefinition

    type private RetainedFlowAttachment =
        { Document: FlowAttachmentSourceDocument
          Parsed: ParsedFlowAttachment
          BaseCallSites: Map<FlowAttachmentBodyRole, FlowCallSite list> }

    type private LoweringState =
        { mutable NextTemporary: int
          mutable NextSyntheticMarker: int64
          mutable AuthoredSpans: Set<SourceSpan>
          mutable SyntheticOrigins: Map<SourceSpan, SourceSpan>
          Signatures: Map<string, Candidate> }

    [<RequireQualifiedAccess>]
    type private ListCallbackOperation =
        | Map
        | Filter
        | Each

    type private BoundArguments =
        { ByParameter: Map<int, FlowExpression>
          HasNamedArguments: bool }

    let private knownTypes (context: Context) =
        Set.union
            (context.CompilerContext.Records |> Map.toSeq |> Seq.map fst |> Set.ofSeq)
            (context.CompilerContext.Scalars |> Map.toSeq |> Seq.map fst |> Set.ofSeq)

    let private fail code message word span expected actual =
        Diagnostics.raiseError code message word span expected actual

    let private diagnostic code message span expected actual : Diagnostic =
        { Code = code
          Message = message
          Word = None
          Span = span
          Expected = expected
          Actual = actual }

    let private spanOfExpression = function
        | FlowExpression.Literal(_, span)
        | FlowExpression.Local(_, span)
        | FlowExpression.Call(_, _, span)
        | FlowExpression.RootCall(_, _, span)
        | FlowExpression.DotCall(_, _, _, span)
        | FlowExpression.If(_, _, _, span)
        | FlowExpression.Container(_, _, _, span)
        | FlowExpression.MatchOption(_, _, _, span)
        | FlowExpression.MatchResult(_, _, _, span) -> span

    let private rememberSpan state span =
        state.AuthoredSpans <- Set.add span state.AuthoredSpans

    let private syntheticSpan state origin =
        rememberSpan state origin
        if state.NextSyntheticMarker < 0L || state.NextSyntheticMarker >= int64 Int32.MaxValue then
            fail "FLOW_SOURCE_MARKER_EXHAUSTED" "Flow lowering exhausted its bounded private source-marker range." None (Some origin) [ "available source marker" ] [ string state.NextSyntheticMarker ]
        let markerColumn = int32 (int64 Int32.MaxValue - state.NextSyntheticMarker)
        state.NextSyntheticMarker <- state.NextSyntheticMarker + 1L
        let generated = { origin with Column = markerColumn; Length = 0 }
        state.SyntheticOrigins <- Map.add generated origin state.SyntheticOrigins
        generated

    let private freshTemporary state origin =
        let name = "$flow$tmp$" + string state.NextTemporary
        state.NextTemporary <- state.NextTemporary + 1
        name, syntheticSpan state origin

    let private internalParameterName index = "$flow$param$" + string index

    let private compilerLocals environment =
        environment
        |> Map.toList
        |> List.map (fun (_, binding) -> binding.InternalName, binding.Type)
        |> Map.ofList

    let private shortName (name: string) =
        let index = name.LastIndexOf('.')
        if index < 0 then name else name.Substring(index + 1)

    let private entryParameterNames (context: Context) name (entry: WordEntry) =
        match entry.Builtin with
        | Some(RecordConstructor typeName) ->
            context.CompilerContext.Records.TryFind typeName
            |> Option.map (fun definition -> definition.Fields |> List.map (fun field -> field.Name))
        | _ -> context.ParameterNames.TryFind name

    let private candidateFromEntry (context: Context) name (entry: WordEntry) =
        let kind =
            match entry.Builtin with
            | Some(BuiltinOp operation) -> SignatureKind.TrustedPrimitive operation
            | Some generated -> SignatureKind.Generated generated
            | None -> SignatureKind.UserWord
        { Name = name
          Inputs = entry.Definition.Inputs
          Outputs = entry.Definition.Outputs
          Effects = entry.Definition.Effects
          Kind = kind
          ParameterNames = entryParameterNames context name entry
          Identity = context.CompilerContext.WordIds.TryFind name
          Revision = entry.Revision
          Span = entry.Definition.Span }

    let private signatureCatalog (context: Context) =
        context.CompilerContext.Words
        |> Map.map (candidateFromEntry context)

    let private freshStateWith (retainedOrigins: Map<SourceSpan, SourceSpan>) signatures =
        let highestRetainedIndex =
            retainedOrigins
            |> Map.toSeq
            |> Seq.choose (fun (marker, _) ->
                if marker.Length = 0 && marker.Column > 0 then Some(int64 Int32.MaxValue - int64 marker.Column)
                else None)
            |> Seq.fold max -1L
        { NextTemporary = 0
          NextSyntheticMarker = highestRetainedIndex + 1L
          AuthoredSpans = Set.empty
          SyntheticOrigins = Map.empty
          Signatures = signatures }

    let private freshState (context: Context) =
        freshStateWith context.SourceOrigins (signatureCatalog context)

    let private candidatesFor (state: LoweringState) (context: Context) requestedName =
        let generatedTypeCandidates =
            let matchingType =
                context.CompilerContext.Records.ContainsKey requestedName
                || context.CompilerContext.Scalars.ContainsKey requestedName
            if not matchingType then []
            else
                state.Signatures
                |> Map.toList
                |> List.choose (fun (name, candidate) ->
                    match candidate.Kind with
                    | SignatureKind.Generated(RecordConstructor typeName) when typeName = requestedName -> Some(name, candidate)
                    | SignatureKind.Generated(ScalarConstructor typeName) when typeName = requestedName -> Some(name, candidate)
                    | _ -> None)
        let namedCandidates =
            if requestedName.Contains('.') then
                state.Signatures.TryFind requestedName |> Option.map (fun candidate -> [ requestedName, candidate ]) |> Option.defaultValue []
            else
                state.Signatures
                |> Map.toList
                |> List.filter (fun (name, _) -> shortName name = requestedName)
        List.distinctBy fst (generatedTypeCandidates @ namedCandidates)
        |> List.map snd
        |> List.sortBy (fun candidate -> candidate.Name)

    let private exactCandidateFor (state: LoweringState) name =
        state.Signatures.TryFind name

    let private validateClosedTypeArgument (context: Context) (argument: FlowTypeArgument) =
        let known = knownTypes context
        let rec validate typeValue =
            match typeValue with
            | TInt | TFloat | TBool | TString | TUnit -> ()
            | TList item | TOption item -> validate item
            | TResult(okType, errorType) -> validate okType; validate errorType
            | TNamed name when known.Contains name -> ()
            | TNamed name -> fail "FLOW_CONSTRUCTOR_UNKNOWN_TYPE" $"Container constructor refers to undeclared type '{name}'." None (Some argument.Span) [ "known record or refined type" ] [ name ]
            | TVar name -> fail "FLOW_CONSTRUCTOR_OPEN_TYPE" "Container constructors require fully closed type arguments." None (Some argument.Span) [ "closed type" ] [ name ]
        validate argument.Type

    let private constructorTypeArguments (context: Context) (kind: FlowContainerConstructor) (typeArguments: FlowTypeArgument list) (span: SourceSpan) =
        let expected =
            match kind with
            | FlowContainerConstructor.ResultOk | FlowContainerConstructor.ResultError -> 2
            | _ -> 1
        if typeArguments.Length <> expected then
            fail "FLOW_CONSTRUCTOR_TYPE_ARITY" "Container constructor has the wrong number of explicit type arguments." None (Some span) [ string expected ] [ string typeArguments.Length ]
        typeArguments |> List.iter (validateClosedTypeArgument context)
        typeArguments |> List.map (fun argument -> argument.Type)

    let private coreConstructor = function
        | FlowContainerConstructor.ListEmpty -> ListEmpty
        | FlowContainerConstructor.ListSingleton -> ListSingleton
        | FlowContainerConstructor.OptionNone -> OptionNone
        | FlowContainerConstructor.OptionSome -> OptionSome
        | FlowContainerConstructor.ResultOk -> ResultOk
        | FlowContainerConstructor.ResultError -> ResultError

    let private unifyType (expected: LangType) (actual: LangType) (substitutions: Map<string, LangType>) =
        let rec unify (expected: LangType) (actual: LangType) (substitutions: Map<string, LangType>) =
            match expected, actual with
            | TVar name, actual ->
                match substitutions.TryFind name with
                | None -> Some(Map.add name actual substitutions)
                | Some existing when existing = actual -> Some substitutions
                | Some existing -> unify existing actual substitutions
            | TList left, TList right
            | TOption left, TOption right -> unify left right substitutions
            | TResult(leftOk, leftError), TResult(rightOk, rightError) ->
                unify leftOk rightOk substitutions |> Option.bind (unify leftError rightError)
            | left, right when left = right -> Some substitutions
            | _ -> None
        unify expected actual substitutions

    let private substituteType (substitutions: Map<string, LangType>) (typeValue: LangType) =
        let rec substitute (typeValue: LangType) =
            match typeValue with
            | TVar name -> substitutions.TryFind name |> Option.map substitute |> Option.defaultValue (TVar name)
            | TList item -> TList(substitute item)
            | TOption item -> TOption(substitute item)
            | TResult(ok, error) -> TResult(substitute ok, substitute error)
            | other -> other
        substitute typeValue

    let private listCallbackOperation = function
        | "map" -> Some ListCallbackOperation.Map
        | "filter" -> Some ListCallbackOperation.Filter
        | "each" -> Some ListCallbackOperation.Each
        | _ -> None

    let private callbackRequiredOutput = function
        | ListCallbackOperation.Map -> None
        | ListCallbackOperation.Filter -> Some TBool
        | ListCallbackOperation.Each -> Some TUnit

    let private callbackResultType operation itemType outputType =
        match operation with
        | ListCallbackOperation.Map -> TList outputType
        | ListCallbackOperation.Filter -> TList itemType
        | ListCallbackOperation.Each -> TUnit

    let private containsOpenType typeValue =
        let rec visit = function
            | TVar _ -> true
            | TList item | TOption item -> visit item
            | TResult(okType, errorType) -> visit okType || visit errorType
            | _ -> false
        visit typeValue

    let private validateWordReferenceShape (state: LoweringState) (reference: FlowWordReference) =
        let pieces = if String.IsNullOrEmpty reference.Name then [||] else reference.Name.Split('.')
        let validSegment (piece: string) =
            piece.Length > 0
            && (Char.IsLetter piece[0] || piece[0] = '_')
            && (piece
                |> Seq.skip 1
                |> Seq.forall (fun character ->
                    Char.IsLetterOrDigit character
                    || character = '_'
                    || character = '-'
                    || character = '?'
                    || character = '!'))
        let hasValidSegments = pieces.Length > 0 && (pieces |> Array.forall validSegment)
        let validQualification =
            match reference.Qualification with
            | FlowWordReferenceQualification.ExplicitShort
            | FlowWordReferenceQualification.AbsoluteRoot -> pieces.Length = 1
            | FlowWordReferenceQualification.NamespaceQualified -> pieces.Length >= 2
        if not hasValidSegments || not validQualification then
            fail "FLOW_CALLBACK_REFERENCE_SHAPE" "Static callback reference qualification does not match its name shape." None (Some reference.Span) [ "namespace::word"; "word shortName"; "::rootName" ] [ reference.Name ]
        rememberSpan state reference.Span

    let private callbackCandidates (state: LoweringState) (reference: FlowWordReference) =
        match reference.Qualification with
        | FlowWordReferenceQualification.ExplicitShort ->
            state.Signatures
            |> Map.toList
            |> List.filter (fun (name, _) -> shortName name = reference.Name)
            |> List.map snd
            |> List.sortBy (fun candidate -> candidate.Name)
        | FlowWordReferenceQualification.NamespaceQualified
        | FlowWordReferenceQualification.AbsoluteRoot ->
            state.Signatures.TryFind reference.Name |> Option.toList

    let private resolveListCallback
        (context: Context)
        (state: LoweringState)
        (reference: FlowWordReference)
        (itemType: LangType)
        (requiredOutput: LangType option)
        : Candidate * LangType =
        validateWordReferenceShape state reference
        let candidates = callbackCandidates state reference
        if List.isEmpty candidates then
            fail "FLOW_UNKNOWN_CALLBACK" $"No static callback word matches '{reference.Name}'." None (Some reference.Span) [] [ reference.Name ]
        if candidates.Length > 1 then
            fail "FLOW_AMBIGUOUS_CALLBACK" $"Static callback '{reference.Name}' resolves to more than one word; use a qualified reference." None (Some reference.Span) [] (candidates |> List.map (fun candidate -> candidate.Name))

        let candidate = candidates.Head
        let inputs = candidate.Inputs
        let outputs = candidate.Outputs
        if inputs.Length <> 1 then
            fail "FLOW_CALLBACK_INPUT_ARITY" $"Callback '{candidate.Name}' must have exactly one input." (Some candidate.Name) (Some reference.Span) [ "one input" ] (inputs |> List.map Types.format)
        if outputs.Length <> 1 then
            fail "FLOW_CALLBACK_OUTPUT_ARITY" $"Callback '{candidate.Name}' must have exactly one output." (Some candidate.Name) (Some reference.Span) [ "one output" ] (outputs |> List.map Types.format)
        let substitutions =
            match unifyType inputs.Head itemType Map.empty with
            | Some values -> values
            | None -> fail "FLOW_CALLBACK_INPUT_TYPE" $"Callback '{candidate.Name}' cannot accept list elements of type {Types.format itemType}." (Some candidate.Name) (Some reference.Span) [ Types.format itemType ] [ Types.format inputs.Head ]
        let outputType = substituteType substitutions outputs.Head
        match requiredOutput with
        | Some expected when expected <> outputType ->
            fail "FLOW_CALLBACK_RESULT_TYPE" $"Callback '{candidate.Name}' must return {Types.format expected}." (Some candidate.Name) (Some reference.Span) [ Types.format expected ] [ Types.format outputType ]
        | _ when containsOpenType outputType ->
            fail "FLOW_CALLBACK_OPEN_OUTPUT" $"Callback '{candidate.Name}' has an output type that cannot be resolved from the list element." (Some candidate.Name) (Some reference.Span) [ "closed output type" ] [ Types.format outputType ]
        | _ -> candidate, outputType

    let private callbackReferenceIn arguments =
        arguments |> List.tryPick (function FlowArgument.WordReference reference -> Some reference | _ -> None)

    let private rejectWordReferenceContext (reference: FlowWordReference) =
        fail "FLOW_CALLBACK_REFERENCE_CONTEXT" "A static callback word reference is not a first-class value and is valid only as the sole argument to a list callback stage." None (Some reference.Span) [] [ reference.Name ]

    let rec private mapArguments (context: Context) (state: LoweringState) (environment: Map<string, Binding>) (candidate: Candidate) (receiverType: LangType option) (arguments: FlowArgument list) (callSpan: SourceSpan) =
        let signature = candidate.Inputs
        let offset = if receiverType.IsSome then 1 else 0
        let expectedArgumentCount = signature.Length - offset
        if expectedArgumentCount < 0 then
            Error(diagnostic "FLOW_DOT_SIGNATURE" $"Word '{candidate.Name}' has no receiver input." (Some callSpan) [ "first input receiver" ] (signature |> List.map Types.format))
        else
            let parameterNames = candidate.ParameterNames
            let suppliedNames =
                match parameterNames with
                | Some names when names.Length = signature.Length -> Some(names |> List.skip offset)
                | _ -> None
            let indexed = ResizeArray<int * FlowExpression>()
            let mutable positionalIndex = offset
            let mutable error: Diagnostic option = None
            let hasNamed = arguments |> List.exists (function FlowArgument.Named _ -> true | _ -> false)
            for argument in arguments do
                match argument with
                | FlowArgument.Positional expression ->
                    if positionalIndex >= signature.Length then
                        error <- Some(diagnostic "FLOW_ARGUMENT_ARITY" $"Call to '{candidate.Name}' has too many positional arguments." (Some callSpan) [ string expectedArgumentCount ] [ string arguments.Length ])
                    else
                        indexed.Add(positionalIndex, expression)
                        positionalIndex <- positionalIndex + 1
                | FlowArgument.Named(name, expression, nameSpan) ->
                    match suppliedNames with
                    | None -> error <- Some(diagnostic "FLOW_NAMED_ARGUMENTS_UNAVAILABLE" $"Named arguments are unavailable for '{candidate.Name}' because its parameter names are not declared in the Flow catalog." (Some nameSpan) [] [ candidate.Name ])
                    | Some names ->
                        match names |> List.tryFindIndex ((=) name) with
                        | None -> error <- Some(diagnostic "FLOW_UNKNOWN_ARGUMENT" $"'{name}' is not a parameter of '{candidate.Name}'." (Some nameSpan) names [ name ])
                        | Some index -> indexed.Add(index + offset, expression)
                | FlowArgument.WordReference reference ->
                    error <- Some(diagnostic "FLOW_CALLBACK_REFERENCE_CONTEXT" "A static callback word reference is not an ordinary value argument." (Some reference.Span) [] [ reference.Name ])
            match error with
            | Some diagnostic -> Error diagnostic
            | None ->
                let duplicates = indexed |> Seq.groupBy fst |> Seq.tryFind (fun (_, group) -> Seq.length group > 1)
                match duplicates with
                | Some(index, _) -> Error(diagnostic "FLOW_DUPLICATE_ARGUMENT" $"Input {index + 1} of '{candidate.Name}' is supplied more than once." (Some callSpan) [] [ string (index + 1) ])
                | None when indexed.Count <> expectedArgumentCount ->
                    Error(diagnostic "FLOW_ARGUMENT_ARITY" $"Call to '{candidate.Name}' is missing one or more inputs." (Some callSpan) [ string expectedArgumentCount ] [ string indexed.Count ])
                | None ->
                    let byParameter = indexed |> Seq.map id |> Map.ofSeq
                    let actualInputs =
                        [ if receiverType.IsSome then yield receiverType.Value
                          for index in offset .. signature.Length - 1 do
                              match byParameter.TryFind index with
                              | Some expression ->
                                  try yield inferExpression context state environment expression
                                  with LanguageException error -> raise (LanguageException error)
                              | None -> () ]
                    let mutable substitutions = Map.empty
                    let mutable mismatch = None
                    List.zip signature actualInputs
                    |> List.iteri (fun index (expected, actual) ->
                        if mismatch.IsNone then
                            match unifyType expected actual substitutions with
                            | Some next -> substitutions <- next
                            | None -> mismatch <- Some(index, expected, actual))
                    match mismatch with
                    | Some(index, expected, actual) ->
                        Error(diagnostic "FLOW_ARGUMENT_TYPE" $"Argument {index + 1} to '{candidate.Name}' has an incompatible type." (Some callSpan) [ Types.format expected ] [ Types.format actual ])
                    | None ->
                        let outputs = candidate.Outputs |> List.map (substituteType substitutions)
                        Ok({ ByParameter = byParameter; HasNamedArguments = hasNamed }, outputs)

    and private inferExpression (context: Context) (state: LoweringState) (environment: Map<string, Binding>) expression =
        let span = spanOfExpression expression
        let outputs = inferOutputs context state environment expression
        match outputs with
        | [ output ] -> output
        | values ->
            let code =
                match expression with
                | FlowExpression.Call _ | FlowExpression.DotCall _ -> "FLOW_CALL_OUTPUT_ARITY"
                | FlowExpression.RootCall _ -> "FLOW_CALL_OUTPUT_ARITY"
                | _ -> "FLOW_EXPRESSION_OUTPUT_ARITY"
            fail code "This Flow expression position requires exactly one output." None (Some span) [ "one output" ] (values |> List.map Types.format)

    and private inferOutputs (context: Context) (state: LoweringState) (environment: Map<string, Binding>) expression =
        let span = spanOfExpression expression
        rememberSpan state span
        match expression with
        | FlowExpression.Literal(literal, _) -> [ literal |> Types.literalValue |> Types.ofValue ]
        | FlowExpression.Local(name, _) ->
            match environment.TryFind name with
            | Some binding -> [ binding.Type ]
            | None -> fail "FLOW_UNKNOWN_LOCAL" $"Local '{name}' is not available before its immutable binding." None (Some span) [] [ name ]
        | FlowExpression.Call(name, arguments, _) ->
            match callbackReferenceIn arguments with
            | Some reference -> rejectWordReferenceContext reference
            | None -> ()
            let _, _, outputs = selectCallOutputs context state environment name None arguments span
            outputs
        | FlowExpression.RootCall(target, arguments, _) ->
            match callbackReferenceIn arguments with
            | Some reference -> rejectWordReferenceContext reference
            | None -> ()
            let _, _, outputs = selectRootCallOutputs context state environment target arguments span
            outputs
        | FlowExpression.DotCall(receiver, stage, arguments, _) ->
            let receiverType = inferExpression context state environment receiver
            match listCallbackOperation stage, arguments with
            | Some operation, [ FlowArgument.WordReference reference ] ->
                validateWordReferenceShape state reference
                match receiverType with
                | TList itemType ->
                    let _, outputType = resolveListCallback context state reference itemType (callbackRequiredOutput operation)
                    [ callbackResultType operation itemType outputType ]
                | actual -> fail "FLOW_CALLBACK_REQUIRES_LIST" $"Static '{stage}' callback stages require a List<T> receiver." None (Some reference.Span) [ "List<T>" ] [ Types.format actual ]
            | _, _ when Option.isSome (callbackReferenceIn arguments) ->
                match callbackReferenceIn arguments with
                | Some reference when listCallbackOperation stage |> Option.isSome ->
                    fail "FLOW_CALLBACK_ARGUMENT_ARITY" $"Static '{stage}' callback syntax requires exactly one positional word reference." None (Some reference.Span) [ "one callback reference" ] [ string arguments.Length ]
                | Some reference -> rejectWordReferenceContext reference
                | None -> failwith "unreachable"
            | _ ->
                let _, _, outputs = selectCallOutputs context state environment stage (Some receiverType) arguments span
                outputs
        | FlowExpression.Container(kind, typeArguments, payload, constructorSpan) ->
            let types = constructorTypeArguments context kind typeArguments constructorSpan
            let outputType, requiredPayloadType =
                match kind, types with
                | FlowContainerConstructor.ListEmpty, [ item ] -> TList item, None
                | FlowContainerConstructor.ListSingleton, [ item ] -> TList item, Some item
                | FlowContainerConstructor.OptionNone, [ item ] -> TOption item, None
                | FlowContainerConstructor.OptionSome, [ item ] -> TOption item, Some item
                | FlowContainerConstructor.ResultOk, [ okType; errorType ] -> TResult(okType, errorType), Some okType
                | FlowContainerConstructor.ResultError, [ okType; errorType ] -> TResult(okType, errorType), Some errorType
                | _ -> fail "FLOW_CONSTRUCTOR_TYPE_ARITY" "Container constructor type arguments do not match its closed type contract." None (Some constructorSpan) [] (types |> List.map Types.format)
            match requiredPayloadType, payload with
            | None, None -> [ outputType ]
            | Some expected, Some expression ->
                let actual = inferExpression context state environment expression
                if expected <> actual then
                    fail "FLOW_CONSTRUCTOR_PAYLOAD_TYPE" "Container constructor payload must exactly match its declared nominal type." None (Some(spanOfExpression expression)) [ Types.format expected ] [ Types.format actual ]
                [ outputType ]
            | None, Some expression -> fail "FLOW_CONSTRUCTOR_ARITY" "This container constructor does not accept a payload." None (Some(spanOfExpression expression)) [ "no payload" ] [ "payload supplied" ]
            | Some _, None -> fail "FLOW_CONSTRUCTOR_ARITY" "This container constructor requires a payload." None (Some constructorSpan) [ "one payload" ] []
        | FlowExpression.MatchOption(scrutinee, someCase, noneCase, matchSpan) ->
            let itemType =
                match inferExpression context state environment scrutinee with
                | TOption item -> item
                | actual -> fail "FLOW_MATCH_REQUIRES_OPTION" "Option match requires an Option<T> scrutinee." None (Some matchSpan) [ "Option<T>" ] [ Types.format actual ]
            if environment.ContainsKey someCase.Name then
                fail "FLOW_MATCH_PAYLOAD_SHADOW" $"Option payload local '{someCase.Name}' cannot shadow an outer local." None (Some someCase.NameSpan) [] [ someCase.Name ]
            let someEnvironment = Map.add someCase.Name { InternalName = someCase.Name; Type = itemType } environment
            let someOutputs = inferStatements context state someEnvironment someCase.Statements
            let noneOutputs = inferStatements context state environment noneCase.Statements
            match someOutputs, noneOutputs with
            | Some someValues, Some noneValues when someValues = noneValues -> someValues
            | Some someValues, Some noneValues -> fail "FLOW_MATCH_BRANCH_TYPE" "Option match cases must produce the same output vector in the same order." None (Some matchSpan) (someValues |> List.map Types.format) (noneValues |> List.map Types.format)
            | _ -> fail "FLOW_MATCH_BRANCH_VALUE" "Both Option match cases must produce a nonempty output vector." None (Some matchSpan) [ "one or more outputs from Some and None" ] []
        | FlowExpression.MatchResult(scrutinee, okCase, errorCase, matchSpan) ->
            let okType, errorType =
                match inferExpression context state environment scrutinee with
                | TResult(okValue, errorValue) -> okValue, errorValue
                | actual -> fail "FLOW_MATCH_REQUIRES_RESULT" "Result match requires a Result<T, E> scrutinee." None (Some matchSpan) [ "Result<T, E>" ] [ Types.format actual ]
            if environment.ContainsKey okCase.Name then
                fail "FLOW_MATCH_PAYLOAD_SHADOW" $"Result ok payload local '{okCase.Name}' cannot shadow an outer local." None (Some okCase.NameSpan) [] [ okCase.Name ]
            if environment.ContainsKey errorCase.Name then
                fail "FLOW_MATCH_PAYLOAD_SHADOW" $"Result error payload local '{errorCase.Name}' cannot shadow an outer local." None (Some errorCase.NameSpan) [] [ errorCase.Name ]
            let okEnvironment = Map.add okCase.Name { InternalName = okCase.Name; Type = okType } environment
            let errorEnvironment = Map.add errorCase.Name { InternalName = errorCase.Name; Type = errorType } environment
            let okOutputs = inferStatements context state okEnvironment okCase.Statements
            let errorOutputs = inferStatements context state errorEnvironment errorCase.Statements
            match okOutputs, errorOutputs with
            | Some okValues, Some errorValues when okValues = errorValues -> okValues
            | Some okValues, Some errorValues -> fail "FLOW_MATCH_BRANCH_TYPE" "Result match cases must produce the same output vector in the same order." None (Some matchSpan) (okValues |> List.map Types.format) (errorValues |> List.map Types.format)
            | _ -> fail "FLOW_MATCH_BRANCH_VALUE" "Both Result match cases must produce a nonempty output vector." None (Some matchSpan) [ "one or more outputs from Ok and Error" ] []
        | FlowExpression.If(condition, thenBody, elseBody, _) ->
            let conditionType = inferExpression context state environment condition
            if conditionType <> TBool then fail "FLOW_IF_CONDITION_TYPE" "Flow if condition must have type Bool." None (Some(spanOfExpression condition)) [ "Bool" ] [ Types.format conditionType ]
            let thenOutputs = inferStatements context state environment thenBody
            let elseOutputs = inferStatements context state environment elseBody
            match thenOutputs, elseOutputs with
            | Some left, Some right when left = right -> left
            | Some left, Some right -> fail "FLOW_IF_BRANCH_TYPE" "Both Flow if branches must produce the same output vector in the same order." None (Some span) (left |> List.map Types.format) (right |> List.map Types.format)
            | None, _ | _, None -> fail "FLOW_IF_BRANCH_VALUE" "Each Flow if branch must produce a nonempty output vector." None (Some span) [ "one or more outputs in each branch" ] []

    and private inferStatements (context: Context) (state: LoweringState) (initialEnvironment: Map<string, Binding>) (statements: FlowStatement list) =
        let mutable environment = initialEnvironment
        let mutable finalValue = None
        for index, statement in statements |> List.indexed do
            match statement with
            | FlowStatement.Let(name, value, statementSpan) ->
                rememberSpan state statementSpan
                if environment.ContainsKey name then fail "FLOW_LOCAL_REBOUND" $"Immutable local '{name}' is already declared in this lexical scope." None (Some statementSpan) [] [ name ]
                let valueType = inferExpression context state environment value
                environment <- Map.add name { InternalName = name; Type = valueType } environment
                finalValue <- None
            | FlowStatement.LetMany(bindings, value, statementSpan) ->
                rememberSpan state statementSpan
                let valueTypes = inferOutputs context state environment value
                if bindings.Length <> valueTypes.Length then
                    fail "FLOW_DESTRUCTURE_ARITY" "A destructuring binding must name every output exactly once." None (Some statementSpan) [ string valueTypes.Length ] [ string bindings.Length ]
                for (name, nameSpan), valueType in List.zip bindings valueTypes do
                    if environment.ContainsKey name then fail "FLOW_LOCAL_REBOUND" $"Immutable local '{name}' is already declared in this lexical scope." None (Some nameSpan) [] [ name ]
                    environment <- Map.add name { InternalName = name; Type = valueType } environment
                finalValue <- None
            | FlowStatement.Evaluate expression ->
                let valueType = inferExpression context state environment expression
                finalValue <- if index = statements.Length - 1 then Some [ valueType ] else None
            | FlowStatement.Return(values, statementSpan) ->
                rememberSpan state statementSpan
                let outputTypes = values |> List.map (inferExpression context state environment)
                finalValue <- if index = statements.Length - 1 then Some outputTypes else None
        finalValue

    and private selectCallOutputs context state environment requestedName receiverType arguments callSpan =
        let candidates = candidatesFor state context requestedName
        if List.isEmpty candidates then
            let code = if receiverType.IsSome then "FLOW_UNKNOWN_DOT_STAGE" else "FLOW_UNKNOWN_CALL"
            fail code $"No word matches '{requestedName}'." None (Some callSpan) [] [ requestedName ]
        let attempts = candidates |> List.map (fun candidate -> candidate, mapArguments context state environment candidate receiverType arguments callSpan)
        let successful = attempts |> List.choose (fun (candidate, result) -> result |> Result.toOption |> Option.map (fun (bound, outputs) -> candidate, bound, outputs))
        match successful with
        | [ candidate, bound, outputs ] -> candidate, bound, outputs
        | [] when candidates.Length = 1 ->
            match attempts.Head with
            | _, Error problem -> raise (LanguageException { problem with Word = Some candidates.Head.Name })
            | _ -> fail "FLOW_NO_MATCHING_CALL" $"Arguments do not match '{requestedName}'." None (Some callSpan) [] [ requestedName ]
        | [] when receiverType.IsSome -> fail "FLOW_NO_MATCHING_DOT_STAGE" $"No '{requestedName}' stage accepts this receiver and its arguments." None (Some callSpan) (receiverType |> Option.map Types.format |> Option.toList) (candidates |> List.map (fun candidate -> candidate.Name))
        | [] -> fail "FLOW_NO_MATCHING_CALL" $"No overload of '{requestedName}' accepts these arguments." None (Some callSpan) [] (candidates |> List.map (fun candidate -> candidate.Name))
        | _ when receiverType.IsSome -> fail "FLOW_AMBIGUOUS_DOT_STAGE" $"Dot stage '{requestedName}' has more than one applicable first-input word; qualify the call explicitly." None (Some callSpan) [] (successful |> List.map (fun (candidate, _, _) -> candidate.Name))
        | _ -> fail "FLOW_AMBIGUOUS_CALL" $"Call '{requestedName}' matches more than one word; use namespace::word qualification." None (Some callSpan) [] (successful |> List.map (fun (candidate, _, _) -> candidate.Name))

    and private selectRootCallOutputs context state environment (target: FlowRootTarget) arguments callSpan =
        let candidate =
            exactCandidateFor state target.Name
            |> Option.defaultWith (fun () ->
                fail "FLOW_UNKNOWN_ROOT_CALL" $"No exact dictionary key '{target.Name}' exists." None (Some target.Span) [ target.Name ] [])
        match mapArguments context state environment candidate None arguments callSpan with
        | Ok(bound, outputs) -> candidate, bound, outputs
        | Error problem -> raise (LanguageException { problem with Word = Some candidate.Name })

    let private createOrigin state span =
        rememberSpan state span
        span

    let private extendPath (FlowAstPath.FlowAstPath segments) segment = FlowAstPath.FlowAstPath(segments @ [ segment ])

    let private fragment expressions callEvents =
        { Expressions = expressions
          CallEvents = callEvents }

    let private appendFragments (fragments: LoweredFragment list) trailingExpressions =
        { Expressions = (fragments |> List.collect (fun value -> value.Expressions)) @ trailingExpressions
          CallEvents = fragments |> List.collect (fun value -> value.CallEvents) }

    let private argumentPath form index =
        match form with
        | FlowCallForm.Direct -> FlowAstPathSegment.CallArgument index
        | FlowCallForm.AbsoluteRoot -> FlowAstPathSegment.RootCallArgument index
        | FlowCallForm.DotStage _ -> FlowAstPathSegment.DotArgument index
        | FlowCallForm.StaticCallback _ -> invalidArg (nameof form) "Static callbacks do not lower through ordinary argument binding."

    let private callEvent path form requestedName candidate span =
        { Path = path
          Span = span
          Form = form
          RequestedName = requestedName
          Candidate = candidate }

    let rec private lowerFlowExpression (context: Context) (state: LoweringState) (environment: Map<string, Binding>) (path: FlowAstPath) expression : LoweredFragment =
        let span = spanOfExpression expression
        rememberSpan state span
        match expression with
        | FlowExpression.Literal(literal, sourceSpan) -> fragment [ Push(literal, sourceSpan) ] []
        | FlowExpression.Local(name, sourceSpan) ->
            match environment.TryFind name with
            | Some binding -> fragment [ Load(binding.InternalName, sourceSpan) ] []
            | None -> fail "FLOW_UNKNOWN_LOCAL" $"Local '{name}' is not available before its immutable binding." None (Some sourceSpan) [] [ name ]
        | FlowExpression.Call(name, arguments, callSpan) ->
            match callbackReferenceIn arguments with
            | Some reference -> rejectWordReferenceContext reference
            | None -> ()
            let candidate, bound, _ = selectCallOutputs context state environment name None arguments callSpan
            lowerResolvedCall context state environment path FlowCallForm.Direct name candidate bound None arguments callSpan
        | FlowExpression.RootCall(target, arguments, callSpan) ->
            match callbackReferenceIn arguments with
            | Some reference -> rejectWordReferenceContext reference
            | None -> ()
            let candidate, bound, _ = selectRootCallOutputs context state environment target arguments callSpan
            lowerResolvedCall context state environment path FlowCallForm.AbsoluteRoot target.Name candidate bound None arguments callSpan
        | FlowExpression.DotCall(receiver, stage, arguments, callSpan) ->
            match listCallbackOperation stage, arguments with
            | Some operation, [ FlowArgument.WordReference reference ] ->
                validateWordReferenceShape state reference
                let receiverType = inferExpression context state environment receiver
                match receiverType with
                | TList itemType ->
                    let candidate, _ = resolveListCallback context state reference itemType (callbackRequiredOutput operation)
                    let receiverFragment = lowerFlowExpression context state environment (extendPath path FlowAstPathSegment.DotReceiver) receiver
                    let listOperation =
                        match operation with
                        | ListCallbackOperation.Map -> MapList(candidate.Name, reference.Span)
                        | ListCallbackOperation.Filter -> FilterList(candidate.Name, reference.Span)
                        | ListCallbackOperation.Each -> EachList(candidate.Name, reference.Span)
                    rememberSpan state reference.Span
                    let event = callEvent (extendPath path (FlowAstPathSegment.DotArgument 0))
                                    (FlowCallForm.StaticCallback(stage, reference.Qualification)) reference.Name candidate reference.Span
                    let lowered = appendFragments [ receiverFragment ] [ listOperation ]
                    { lowered with CallEvents = lowered.CallEvents @ [ event ] }
                | actual -> fail "FLOW_CALLBACK_REQUIRES_LIST" $"Static '{stage}' callback stages require a List<T> receiver." None (Some reference.Span) [ "List<T>" ] [ Types.format actual ]
            | _, _ when Option.isSome (callbackReferenceIn arguments) ->
                match callbackReferenceIn arguments with
                | Some reference when listCallbackOperation stage |> Option.isSome ->
                    fail "FLOW_CALLBACK_ARGUMENT_ARITY" $"Static '{stage}' callback syntax requires exactly one positional word reference." None (Some reference.Span) [ "one callback reference" ] [ string arguments.Length ]
                | Some reference -> rejectWordReferenceContext reference
                | None -> failwith "unreachable"
            | _ ->
                let receiverType = inferExpression context state environment receiver
                let candidate, bound, _ = selectCallOutputs context state environment stage (Some receiverType) arguments callSpan
                lowerResolvedCall context state environment path (FlowCallForm.DotStage stage) stage candidate bound (Some receiver) arguments callSpan
        | FlowExpression.Container(kind, typeArguments, payload, constructorSpan) ->
            inferExpression context state environment expression |> ignore
            let payloadFragment = payload |> Option.map (lowerFlowExpression context state environment (extendPath path FlowAstPathSegment.ContainerPayload))
            appendFragments (payloadFragment |> Option.toList) [ ConstructContainer(coreConstructor kind, typeArguments |> List.map (fun argument -> argument.Type), constructorSpan) ]
        | FlowExpression.MatchOption(scrutinee, someCase, noneCase, matchSpan) ->
            inferOutputs context state environment expression |> ignore
            let someType = inferExpression context state environment scrutinee |> function | TOption item -> item | _ -> failwith "validated option match changed type"
            let someEnvironment = Map.add someCase.Name { InternalName = someCase.Name; Type = someType } environment
            let someFragment, _ = lowerStatements context state someEnvironment (extendPath path (FlowAstPathSegment.OptionSomeStatement 0)) someCase.Statements
            let noneFragment, _ = lowerStatements context state environment (extendPath path (FlowAstPathSegment.OptionNoneStatement 0)) noneCase.Statements
            let someScope = Scope(someFragment.Expressions, syntheticSpan state someCase.Span)
            let noneScope = Scope(noneFragment.Expressions, syntheticSpan state noneCase.Span)
            let scrutineeFragment = lowerFlowExpression context state environment (extendPath path FlowAstPathSegment.OptionScrutinee) scrutinee
            { Expressions = scrutineeFragment.Expressions @ [ MatchOption(someCase.Name, [ someScope ], [ noneScope ], matchSpan) ]
              CallEvents = scrutineeFragment.CallEvents @ someFragment.CallEvents @ noneFragment.CallEvents }
        | FlowExpression.MatchResult(scrutinee, okCase, errorCase, matchSpan) ->
            inferOutputs context state environment expression |> ignore
            let okType, errorType =
                match inferExpression context state environment scrutinee with
                | TResult(okValue, errorValue) -> okValue, errorValue
                | _ -> failwith "validated result match changed type"
            let okEnvironment = Map.add okCase.Name { InternalName = okCase.Name; Type = okType } environment
            let errorEnvironment = Map.add errorCase.Name { InternalName = errorCase.Name; Type = errorType } environment
            let okFragment, _ = lowerStatements context state okEnvironment (extendPath path (FlowAstPathSegment.ResultOkStatement 0)) okCase.Statements
            let errorFragment, _ = lowerStatements context state errorEnvironment (extendPath path (FlowAstPathSegment.ResultErrorStatement 0)) errorCase.Statements
            let okScope = Scope(okFragment.Expressions, syntheticSpan state okCase.Span)
            let errorScope = Scope(errorFragment.Expressions, syntheticSpan state errorCase.Span)
            let scrutineeFragment = lowerFlowExpression context state environment (extendPath path FlowAstPathSegment.ResultScrutinee) scrutinee
            { Expressions = scrutineeFragment.Expressions @ [ MatchResult(okCase.Name, errorCase.Name, [ okScope ], [ errorScope ], matchSpan) ]
              CallEvents = scrutineeFragment.CallEvents @ okFragment.CallEvents @ errorFragment.CallEvents }
        | FlowExpression.If(condition, thenStatements, elseStatements, ifSpan) ->
            inferOutputs context state environment expression |> ignore
            let conditionType = inferExpression context state environment condition
            if conditionType <> TBool then fail "FLOW_IF_CONDITION_TYPE" "Flow if condition must have type Bool." None (Some(spanOfExpression condition)) [ "Bool" ] [ Types.format conditionType ]
            let thenFragment, _ = lowerStatements context state environment (extendPath path (FlowAstPathSegment.IfThenStatement 0)) thenStatements
            let elseFragment, _ = lowerStatements context state environment (extendPath path (FlowAstPathSegment.IfElseStatement 0)) elseStatements
            let thenScope = Scope(thenFragment.Expressions, syntheticSpan state ifSpan)
            let elseScope = Scope(elseFragment.Expressions, syntheticSpan state ifSpan)
            let conditionFragment = lowerFlowExpression context state environment (extendPath path FlowAstPathSegment.IfCondition) condition
            { Expressions = conditionFragment.Expressions @ [ If([ thenScope ], [ elseScope ], ifSpan) ]
              CallEvents = conditionFragment.CallEvents @ thenFragment.CallEvents @ elseFragment.CallEvents }

    and private lowerResolvedCall context state environment path form requestedName candidate (bound: BoundArguments) receiver arguments callSpan =
        let argumentFormalIndexes =
            let offset = if receiver.IsSome then 1 else 0
            let mutable positional = offset
            arguments
            |> List.map (function
                | FlowArgument.Positional _ -> let current = positional in positional <- positional + 1; current
                | FlowArgument.Named(name, _, _) ->
                    match candidate.ParameterNames with
                    | Some names when names.Length = candidate.Inputs.Length ->
                        names |> List.skip offset |> List.tryFindIndex ((=) name) |> Option.map (fun value -> value + offset) |> Option.defaultValue -1
                    | _ -> -1
                | FlowArgument.WordReference reference -> rejectWordReferenceContext reference)
        let targetName = candidate.Name
        let receiverPath = extendPath path FlowAstPathSegment.DotReceiver
        let valuePath index = extendPath path (argumentPath form index)
        let lowerArgument index = function
            | FlowArgument.Positional value | FlowArgument.Named(_, value, _) ->
                lowerFlowExpression context state environment (valuePath index) value
            | FlowArgument.WordReference reference -> rejectWordReferenceContext reference
        let event = callEvent path form requestedName candidate callSpan
        if not bound.HasNamedArguments then
            let receiverFragment = receiver |> Option.map (lowerFlowExpression context state environment receiverPath)
            let argumentFragments = arguments |> List.mapi lowerArgument
            let fragments = (receiverFragment |> Option.toList) @ argumentFragments
            let lowered = appendFragments fragments [ Call(targetName, callSpan) ]
            { lowered with CallEvents = lowered.CallEvents @ [ event ] }
        else
            let wrapperSpan = syntheticSpan state callSpan
            let mutable tempByParameter = Map.empty
            let setup = ResizeArray<Expr>()
            let evaluatedFragments = ResizeArray<LoweredFragment>()
            match receiver with
            | Some expression ->
                let receiverOrigin = spanOfExpression expression
                let temporary, tempSpan = freshTemporary state receiverOrigin
                let receiverFragment = lowerFlowExpression context state environment receiverPath expression
                evaluatedFragments.Add receiverFragment
                setup.AddRange receiverFragment.Expressions
                setup.Add(Let(temporary, tempSpan))
                tempByParameter <- Map.add 0 temporary tempByParameter
            | None -> ()
            for index, (argument, formalIndex) in List.zip arguments argumentFormalIndexes |> List.indexed do
                let value =
                    match argument with
                    | FlowArgument.Positional value | FlowArgument.Named(_, value, _) -> value
                    | FlowArgument.WordReference reference -> rejectWordReferenceContext reference
                let origin = match argument with | FlowArgument.Named(_, _, namedSpan) -> namedSpan | _ -> spanOfExpression value
                let temporary, tempSpan = freshTemporary state origin
                let argumentFragment = lowerArgument index argument
                evaluatedFragments.Add argumentFragment
                setup.AddRange argumentFragment.Expressions
                setup.Add(Let(temporary, tempSpan))
                tempByParameter <- Map.add formalIndex temporary tempByParameter
            let loads =
                [ 0 .. candidate.Inputs.Length - 1 ]
                |> List.map (fun parameterIndex ->
                    match tempByParameter.TryFind parameterIndex with
                    | Some temporary -> Load(temporary, syntheticSpan state callSpan)
                    | None ->
                        let argument = bound.ByParameter.TryFind parameterIndex
                        match argument with
                        | Some _ -> fail "FLOW_ARGUMENT_REORDER_INVARIANT" "A named argument was not evaluated into its source-order temporary." (Some targetName) (Some callSpan) [] [ string parameterIndex ]
                        | None -> fail "FLOW_ARGUMENT_VALUE_MISSING" "A call input has no lowered value." (Some targetName) (Some callSpan) [] [ string parameterIndex ])
            { Expressions = [ Expr.Scope(List.ofSeq setup @ loads @ [ Call(targetName, callSpan) ], wrapperSpan) ]
              CallEvents = (evaluatedFragments |> Seq.collect (fun value -> value.CallEvents) |> Seq.toList) @ [ event ] }

    and private lowerStatements context state initialEnvironment path statements =
        let mutable environment = initialEnvironment
        let output = ResizeArray<Expr>()
        let callEvents = ResizeArray<FlowCallEvent>()
        for index, statement in statements |> List.indexed do
            let statementPath = extendPath path (FlowAstPathSegment.BlockStatement index)
            match statement with
            | FlowStatement.Let(name, value, statementSpan) ->
                rememberSpan state statementSpan
                if environment.ContainsKey name then fail "FLOW_LOCAL_REBOUND" $"Immutable local '{name}' is already declared in this lexical scope." None (Some statementSpan) [] [ name ]
                let valueType = inferExpression context state environment value
                let valueFragment = lowerFlowExpression context state environment (extendPath statementPath FlowAstPathSegment.LetInitializer) value
                output.AddRange valueFragment.Expressions
                callEvents.AddRange valueFragment.CallEvents
                output.Add(Let(name, statementSpan))
                environment <- Map.add name { InternalName = name; Type = valueType } environment
            | FlowStatement.LetMany(bindings, value, statementSpan) ->
                rememberSpan state statementSpan
                let valueTypes = inferOutputs context state environment value
                if bindings.Length <> valueTypes.Length then
                    fail "FLOW_DESTRUCTURE_ARITY" "A destructuring binding must name every output exactly once." None (Some statementSpan) [ string valueTypes.Length ] [ string bindings.Length ]
                for name, nameSpan in bindings do
                    rememberSpan state nameSpan
                    if environment.ContainsKey name then fail "FLOW_LOCAL_REBOUND" $"Immutable local '{name}' is already declared in this lexical scope." None (Some nameSpan) [] [ name ]
                let valueFragment = lowerFlowExpression context state environment (extendPath statementPath FlowAstPathSegment.DestructureInitializer) value
                output.AddRange valueFragment.Expressions
                callEvents.AddRange valueFragment.CallEvents
                for (name, nameSpan), valueType in List.zip (List.rev bindings) (List.rev valueTypes) do
                    output.Add(Let(name, nameSpan))
                    environment <- Map.add name { InternalName = name; Type = valueType } environment
            | FlowStatement.Evaluate expression ->
                inferExpression context state environment expression |> ignore
                let valueFragment = lowerFlowExpression context state environment (extendPath statementPath FlowAstPathSegment.EvaluateExpression) expression
                output.AddRange valueFragment.Expressions
                callEvents.AddRange valueFragment.CallEvents
                if index < statements.Length - 1 then
                    let origin = spanOfExpression expression
                    let temporary, tempSpan = freshTemporary state origin
                    let scopeSpan = syntheticSpan state origin
                    output.Add(Scope([ Let(temporary, tempSpan) ], scopeSpan))
            | FlowStatement.Return(values, statementSpan) ->
                rememberSpan state statementSpan
                for value in values do inferExpression context state environment value |> ignore
                for returnIndex, value in List.indexed values do
                    let valueFragment = lowerFlowExpression context state environment (extendPath statementPath (FlowAstPathSegment.ReturnOutput returnIndex)) value
                    output.AddRange valueFragment.Expressions
                    callEvents.AddRange valueFragment.CallEvents
        fragment (List.ofSeq output) (List.ofSeq callEvents), environment

    let private makeProjection (state: LoweringState) : FlowSourceProjection =
        { AuthoredSpans = state.AuthoredSpans
          SyntheticOrigins = state.SyntheticOrigins }

    let private mergeOrigins (context: Context) (projection: FlowSourceProjection) : Map<SourceSpan, SourceSpan> =
        let collision = projection.SyntheticOrigins |> Map.toSeq |> Seq.tryFind (fun (marker, _) -> context.SourceOrigins.ContainsKey marker)
        match collision with
        | Some _ -> fail "FLOW_SOURCE_ORIGIN_COLLISION" "Internal Flow source markers must remain unique across one compiler snapshot." None None [] [ "marker collision" ]
        | None -> Map.fold (fun found marker origin -> Map.add marker origin found) context.SourceOrigins projection.SyntheticOrigins

    let private remapAttachmentDiagnostic (sourceOrigins: Map<SourceSpan, SourceSpan>) action =
        try action ()
        with
        | LanguageException diagnostic ->
            match diagnostic.Span |> Option.bind (fun source -> sourceOrigins.TryFind source) with
            | Some authored -> raise (LanguageException { diagnostic with Span = Some authored })
            | None -> raise (LanguageException diagnostic)

    let private sourceSites (sourceMap: Map<SourceSiteId, IrSourceSite>) =
        sourceMap |> Map.map (fun _ source -> source.SiteSpan)

    type private IrAuthoredCall =
        { Site: SourceSiteId
          Call: IrResolvedCall
          Operation: IrOperation }

    let private authoredCallsInBlock (block: IrBlock) =
        let rec walk (current: IrBlock) =
            current.Code
            |> List.collect (fun instruction ->
                let authored =
                    match instruction.Operation with
                    | IrOperation.Call call
                    | IrOperation.ListMap(call, _, _)
                    | IrOperation.ListFilter(call, _)
                    | IrOperation.ListEach(call, _)
                    | IrOperation.MakeRecord(call, _)
                    | IrOperation.GetRecordField(call, _, _)
                    | IrOperation.UnwrapScalar(call, _) ->
                        [ { Site = instruction.Site; Call = call; Operation = instruction.Operation } ]
                    | IrOperation.WrapScalar(call, _, _) ->
                        // The validator is an implicit type contract, not an
                        // authored Flow call site.
                        [ { Site = instruction.Site; Call = call; Operation = instruction.Operation } ]
                    | _ -> []
                let nested =
                    match instruction.Operation with
                    | IrOperation.Scope inner -> walk inner
                    | IrOperation.If(thenBlock, elseBlock) -> walk thenBlock @ walk elseBlock
                    | IrOperation.MatchOption(_, someBlock, noneBlock) -> walk someBlock @ walk noneBlock
                    | IrOperation.MatchResult(_, _, okBlock, errorBlock) -> walk okBlock @ walk errorBlock
                    | _ -> []
                authored @ nested)
        walk block

    let private targetIdentityOfCandidate ownerName (candidate: Candidate) =
        match candidate.Kind with
        | SignatureKind.TrustedPrimitive operation -> FlowCallTargetIdentity.Primitive(PrimitiveId operation)
        | SignatureKind.Generated _ ->
            match candidate.Identity with
            | Some identity -> FlowCallTargetIdentity.GeneratedWord identity
            | None -> fail "FLOW_BINDING_TARGET_ID_MISSING" "A generated Flow call has no stable target identity." (Some ownerName) (Some candidate.Span) [ "generated WordId" ] [ candidate.Name ]
        | SignatureKind.UserWord ->
            match candidate.Identity with
            | Some identity -> FlowCallTargetIdentity.UserWord identity
            | None -> fail "FLOW_BINDING_TARGET_ID_MISSING" "A user Flow call has no stable target identity." (Some ownerName) (Some candidate.Span) [ "user WordId" ] [ candidate.Name ]

    let private revisionOfCandidate (candidate: Candidate) =
        match candidate.Kind with
        | SignatureKind.TrustedPrimitive _ -> None
        | SignatureKind.Generated _ | SignatureKind.UserWord -> Some candidate.Revision

    let private callSiteOfEvent ownerName (event: FlowCallEvent) : FlowCallSite =
        { Path = event.Path
          Span = event.Span
          Form = event.Form
          RequestedName = event.RequestedName
          Target = targetIdentityOfCandidate ownerName event.Candidate
          TargetRevision = revisionOfCandidate event.Candidate }

    let private identityOfResolvedCall (call: IrResolvedCall) =
        match call.ResolvedTarget with
        | UserWordTarget(identity, _) -> FlowCallTargetIdentity.UserWord identity
        | PrimitiveTarget identity -> FlowCallTargetIdentity.Primitive identity
        | GeneratedWordTarget(identity, _) -> FlowCallTargetIdentity.GeneratedWord identity

    let private revisionOfResolvedCall (call: IrResolvedCall) =
        match call.ResolvedTarget with
        | UserWordTarget(_, revision)
        | GeneratedWordTarget(_, revision) -> Some revision
        | PrimitiveTarget _ -> None

    let private operationMatchesCandidate form (candidate: Candidate) operation =
        match form with
        | FlowCallForm.StaticCallback("map", _) -> match operation with | IrOperation.ListMap _ -> true | _ -> false
        | FlowCallForm.StaticCallback("filter", _) -> match operation with | IrOperation.ListFilter _ -> true | _ -> false
        | FlowCallForm.StaticCallback("each", _) -> match operation with | IrOperation.ListEach _ -> true | _ -> false
        | FlowCallForm.StaticCallback _ -> false
        | _ ->
            match candidate.Kind, operation with
            | SignatureKind.Generated(RecordConstructor _), IrOperation.MakeRecord _ -> true
            | SignatureKind.Generated(RecordAccessor _), IrOperation.GetRecordField _ -> true
            | SignatureKind.Generated(ScalarConstructor _), IrOperation.WrapScalar _ -> true
            | SignatureKind.Generated(ScalarAccessor _), IrOperation.UnwrapScalar _ -> true
            | SignatureKind.Generated _, _ -> false
            | (SignatureKind.TrustedPrimitive _ | SignatureKind.UserWord), IrOperation.Call _ -> true
            | _ -> false

    let private expectedSourceKind = function
        | FlowCallForm.StaticCallback("map", _) -> "list-map"
        | FlowCallForm.StaticCallback("filter", _) -> "list-filter"
        | FlowCallForm.StaticCallback("each", _) -> "list-each"
        | FlowCallForm.StaticCallback _ -> "invalid-list-callback"
        | _ -> "call"

    let private reconcileCallEvents (program: VerifiedIrProgram) ownerName ownerId ownerRevision (events: FlowCallEvent list) =
        match events |> List.countBy (fun event -> event.Path) |> List.tryFind (fun (_, count) -> count > 1) with
        | Some(path, _) -> fail "FLOW_BINDING_PATH_DUPLICATE" "Two authored call events resolved to the same structural AST path." (Some ownerName) None [] [ sprintf "%A" path ]
        | None -> ()
        let programData = VerifiedIrProgram.inspect program
        let functionValue =
            programData.FunctionsById.TryFind ownerId
            |> Option.defaultWith (fun () -> fail "FLOW_BINDING_OWNER_MISSING" "The verified program has no Flow binding owner function." (Some ownerName) None [ sprintf "%A" ownerId ] [])
        if functionValue.FunctionRevision <> ownerRevision then
            fail "FLOW_BINDING_OWNER_REVISION" "The verified owner revision does not match its Flow call-binding source." (Some ownerName) None [ string ownerRevision ] [ string functionValue.FunctionRevision ]
        let actual = authoredCallsInBlock functionValue.FunctionBody
        if actual.Length <> events.Length then
            fail "FLOW_BINDING_IR_CALL_COUNT" "The captured Flow call events do not cover the verified owner's call-like IR operations." (Some ownerName) None
                [ string events.Length ] [ string actual.Length ]
        for index, (event, actualCall) in List.zip events actual |> List.indexed do
            let site =
                programData.SourceMap.TryFind actualCall.Site
                |> Option.defaultWith (fun () -> fail "FLOW_BINDING_IR_SITE_MISSING" "A verified call-like operation has no source-map entry." (Some ownerName) (Some event.Span) [] [ sprintf "%A" actualCall.Site ])
            if site.SiteOwner <> Some ownerId then
                fail "FLOW_BINDING_IR_OWNER_MISMATCH" "A call-like IR source site belongs to a different word than the captured Flow event." (Some ownerName) (Some event.Span)
                    [ sprintf "%A" ownerId ] [ sprintf "%A" site.SiteOwner ]
            if site.SiteSpan <> event.Span then
                fail "FLOW_BINDING_IR_SPAN_MISMATCH" "A call-like IR source span does not match its ordered Flow event." (Some ownerName) (Some event.Span)
                    [ sprintf "%A" event.Span ] [ sprintf "%A" site.SiteSpan ]
            if site.SourceKind <> expectedSourceKind event.Form then
                fail "FLOW_BINDING_IR_KIND_MISMATCH" "A call-like IR source kind does not match the authored Flow call form." (Some ownerName) (Some event.Span)
                    [ expectedSourceKind event.Form ] [ site.SourceKind ]
            let expectedTarget = targetIdentityOfCandidate ownerName event.Candidate
            let actualTarget = identityOfResolvedCall actualCall.Call
            if actualTarget <> expectedTarget then
                fail "FLOW_BINDING_IR_TARGET_MISMATCH" "Verified IR resolved a Flow call to a different stable target identity." (Some ownerName) (Some event.Span)
                    [ sprintf "%A" expectedTarget ] [ sprintf "%A" actualTarget ]
            let expectedRevision = revisionOfCandidate event.Candidate
            let actualRevision = revisionOfResolvedCall actualCall.Call
            if actualRevision <> expectedRevision then
                fail "FLOW_BINDING_IR_TARGET_REVISION" "Verified IR resolved a Flow call to a different target revision than the final signature catalog." (Some ownerName) (Some event.Span)
                    [ sprintf "%A" expectedRevision ] [ sprintf "%A" actualRevision ]
            if actualCall.Call.ResolvedName <> event.Candidate.Name then
                fail "FLOW_BINDING_IR_TARGET_NAME" "Verified IR resolved a Flow call through a different dictionary name than the selected candidate." (Some ownerName) (Some event.Span)
                    [ event.Candidate.Name ] [ actualCall.Call.ResolvedName ]
            if not (operationMatchesCandidate event.Form event.Candidate actualCall.Operation) then
                fail "FLOW_BINDING_IR_OPERATION_MISMATCH" "Verified IR lowered a Flow call through a different operation kind." (Some ownerName) (Some event.Span)
                    [ sprintf "%A" event.Candidate.Kind; sprintf "%A" event.Form ] [ sprintf "%A" actualCall.Operation ]
        events |> List.map (callSiteOfEvent ownerName)

    let private reconcileDetachedCallEvents
        (ownerName: string)
        (verifiedBody: VerifiedIrBody)
        (events: FlowCallEvent list)
        : FlowCallSite list =
        match events |> List.countBy (fun event -> event.Path) |> List.tryFind (fun (_, count) -> count > 1) with
        | Some(path, _) ->
            fail "FLOW_ATTACHMENT_BINDING_PATH_DUPLICATE" "Two authored calls in one detached attachment body resolved to the same structural AST path." (Some ownerName) None [] [ sprintf "%A" path ]
        | None -> ()
        let body = VerifiedIrBody.inspect verifiedBody
        let actual = authoredCallsInBlock body.BodyBlock
        if actual.Length <> events.Length then
            fail "FLOW_ATTACHMENT_BINDING_IR_CALL_COUNT" "Captured attachment call events do not cover its verified detached body." (Some ownerName) None
                [ string events.Length ] [ string actual.Length ]
        for index, (event, actualCall) in List.zip events actual |> List.indexed do
            let source =
                body.BodySourceMap.TryFind actualCall.Site
                |> Option.defaultWith (fun () -> fail "FLOW_ATTACHMENT_BINDING_IR_SITE_MISSING" "A detached attachment call-like operation has no source-map entry." (Some ownerName) (Some event.Span) [] [ sprintf "%A" actualCall.Site ])
            if source.SiteOwner.IsSome then
                fail "FLOW_ATTACHMENT_BINDING_IR_OWNER_MISMATCH" "A detached attachment source site must not claim to belong to a dictionary function." (Some ownerName) (Some event.Span)
                    [ "SiteOwner=None" ] [ sprintf "%A" source.SiteOwner ]
            if source.SiteSpan <> event.Span then
                fail "FLOW_ATTACHMENT_BINDING_IR_SPAN_MISMATCH" "A detached call-like IR source span does not match its ordered Flow event." (Some ownerName) (Some event.Span)
                    [ sprintf "%A" event.Span ] [ sprintf "%A" source.SiteSpan ]
            if source.SourceKind <> expectedSourceKind event.Form then
                fail "FLOW_ATTACHMENT_BINDING_IR_KIND_MISMATCH" "A detached call-like IR source kind does not match the authored Flow call form." (Some ownerName) (Some event.Span)
                    [ expectedSourceKind event.Form ] [ source.SourceKind ]
            let expectedTarget = targetIdentityOfCandidate ownerName event.Candidate
            let actualTarget = identityOfResolvedCall actualCall.Call
            if actualTarget <> expectedTarget then
                fail "FLOW_ATTACHMENT_BINDING_IR_TARGET_MISMATCH" "Verified detached IR resolved an authored call to a different stable target identity." (Some ownerName) (Some event.Span)
                    [ sprintf "%A" expectedTarget ] [ sprintf "%A" actualTarget ]
            let expectedRevision = revisionOfCandidate event.Candidate
            let actualRevision = revisionOfResolvedCall actualCall.Call
            if actualRevision <> expectedRevision then
                fail "FLOW_ATTACHMENT_BINDING_IR_TARGET_REVISION" "Verified detached IR resolved a call to a different target revision than the final signature catalog." (Some ownerName) (Some event.Span)
                    [ sprintf "%A" expectedRevision ] [ sprintf "%A" actualRevision ]
            if actualCall.Call.ResolvedName <> event.Candidate.Name then
                fail "FLOW_ATTACHMENT_BINDING_IR_TARGET_NAME" "Verified detached IR resolved an authored call through a different dictionary name than the selected candidate." (Some ownerName) (Some event.Span)
                    [ event.Candidate.Name ] [ actualCall.Call.ResolvedName ]
            if not (operationMatchesCandidate event.Form event.Candidate actualCall.Operation) then
                fail "FLOW_ATTACHMENT_BINDING_IR_OPERATION_MISMATCH" "Verified detached IR lowered an authored call through a different operation kind." (Some ownerName) (Some event.Span)
                    [ sprintf "%A" event.Candidate.Kind; sprintf "%A" event.Form ] [ sprintf "%A" actualCall.Operation ]
        events |> List.map (callSiteOfEvent ownerName)

    let rec private semanticExpressions (expressions: Expr list) : SemanticExpr list =
        let rec normalize = function
            | Push(literal, _) -> SemanticPush literal
            | Call(name, _) -> SemanticCall name
            | ConstructContainer(kind, typeArguments, _) -> SemanticConstruct(kind, typeArguments)
            | MapList(name, _) -> SemanticMapList name
            | FilterList(name, _) -> SemanticFilterList name
            | EachList(name, _) -> SemanticEachList name
            | Let(name, _) -> SemanticLet name
            | Load(name, _) -> SemanticLoad name
            | If(thenBranch, elseBranch, _) -> SemanticIf(semanticExpressions thenBranch, semanticExpressions elseBranch)
            | Scope(body, _) -> SemanticScope(semanticExpressions body)
            | MatchOption(name, someBranch, noneBranch, _) -> SemanticMatchOption(name, semanticExpressions someBranch, semanticExpressions noneBranch)
            | MatchResult(okName, errorName, okBranch, errorBranch, _) ->
                SemanticMatchResult(okName, errorName, semanticExpressions okBranch, semanticExpressions errorBranch)
        expressions |> List.map normalize

    let private validateFlowSourceDocument (document: FlowSourceDocument) =
        if String.IsNullOrWhiteSpace document.OwnerName then
            fail "FLOW_SOURCE_OWNER_INVALID" "An authored Flow source document requires an owner name." None None [ "nonempty word name" ] [ document.OwnerName ]
        if String.IsNullOrWhiteSpace document.SourceFile then
            fail "FLOW_SOURCE_FILE_INVALID" "An authored Flow source document requires a source file label for diagnostics." (Some document.OwnerName) None [ "nonempty source file" ] [ document.SourceFile ]
        if document.OwnerRevision < 0 then
            fail "FLOW_SOURCE_REVISION_INVALID" "An authored Flow source revision must be nonnegative." (Some document.OwnerName) None [ "nonnegative revision" ] [ string document.OwnerRevision ]
        match document.OwnerId with
        | WordId raw when String.IsNullOrWhiteSpace raw ->
            fail "FLOW_SOURCE_OWNER_ID_INVALID" "An authored Flow source document requires a nonempty stable owner ID." (Some document.OwnerName) None [ "nonempty WordId" ] [ raw ]
        | _ -> ()
        if document.Reference.Kind <> StorageObjectKind.WordDefinition then
            fail "FLOW_SOURCE_KIND_MISMATCH" "Flow word source references must identify word-definition objects." (Some document.OwnerName) None
                [ string StorageObjectKind.WordDefinition ] [ string document.Reference.Kind ]
        if isNull document.Content then
            fail "FLOW_SOURCE_CONTENT_INVALID" "Authored Flow source content cannot be null." (Some document.OwnerName) None [ "UTF-8 source text" ] [ "null" ]
        let actualReference =
            try Storage.sourceObject StorageObjectKind.WordDefinition document.Content |> fun source -> source.Reference
            with
            | :? EncoderFallbackException ->
                fail "FLOW_SOURCE_UTF8_INVALID" "Authored Flow source must encode as strict UTF-8 without replacement characters." (Some document.OwnerName) None [ "valid UTF-16 source text" ] [ "unpaired surrogate" ]
        if actualReference <> document.Reference then
            fail "FLOW_SOURCE_HASH_MISMATCH" "The supplied Flow source reference does not match the exact strict UTF-8 source bytes." (Some document.OwnerName) None
                [ actualReference.Hash ] [ document.Reference.Hash ]
        match FlowParser.parseWord document.SourceFile document.Content with
        | Ok definition when definition.Name = document.OwnerName -> definition
        | Ok definition ->
            fail "FLOW_SOURCE_OWNER_NAME_MISMATCH" "The parsed Flow source word name differs from its host-declared owner name." (Some document.OwnerName) (Some definition.Span)
                [ document.OwnerName ] [ definition.Name ]
        | Error problem -> raise (LanguageException { problem with Word = Some document.OwnerName })

    let private flowPathsAtSpan (flowWord: FlowWordDefinition) (targetSpan: SourceSpan) =
        let found = ResizeArray<FlowAstPath>()
        let add path span =
            if span = targetSpan then found.Add path
        let rec walkExpression path expression =
            add path (spanOfExpression expression)
            match expression with
            | FlowExpression.Call(_, arguments, _) ->
                arguments |> List.iteri (fun index argument -> walkArgument path FlowCallForm.Direct index argument)
            | FlowExpression.RootCall(_, arguments, _) ->
                arguments |> List.iteri (fun index argument -> walkArgument path FlowCallForm.AbsoluteRoot index argument)
            | FlowExpression.DotCall(receiver, stage, arguments, _) ->
                walkExpression (extendPath path FlowAstPathSegment.DotReceiver) receiver
                arguments |> List.iteri (fun index argument -> walkArgument path (FlowCallForm.DotStage stage) index argument)
            | FlowExpression.If(condition, thenStatements, elseStatements, _) ->
                walkExpression (extendPath path FlowAstPathSegment.IfCondition) condition
                walkStatements (extendPath path (FlowAstPathSegment.IfThenStatement 0)) thenStatements
                walkStatements (extendPath path (FlowAstPathSegment.IfElseStatement 0)) elseStatements
            | FlowExpression.Container(_, _, payload, _) ->
                payload |> Option.iter (walkExpression (extendPath path FlowAstPathSegment.ContainerPayload))
            | FlowExpression.MatchOption(scrutinee, someCase, noneCase, _) ->
                walkExpression (extendPath path FlowAstPathSegment.OptionScrutinee) scrutinee
                walkStatements (extendPath path (FlowAstPathSegment.OptionSomeStatement 0)) someCase.Statements
                walkStatements (extendPath path (FlowAstPathSegment.OptionNoneStatement 0)) noneCase.Statements
            | FlowExpression.MatchResult(scrutinee, okCase, errorCase, _) ->
                walkExpression (extendPath path FlowAstPathSegment.ResultScrutinee) scrutinee
                walkStatements (extendPath path (FlowAstPathSegment.ResultOkStatement 0)) okCase.Statements
                walkStatements (extendPath path (FlowAstPathSegment.ResultErrorStatement 0)) errorCase.Statements
            | FlowExpression.Literal _ | FlowExpression.Local _ -> ()
        and walkArgument path form index argument =
            let argumentPath = extendPath path (argumentPath form index)
            match argument with
            | FlowArgument.Positional expression -> walkExpression argumentPath expression
            | FlowArgument.Named(_, expression, nameSpan) ->
                add argumentPath nameSpan
                walkExpression argumentPath expression
            | FlowArgument.WordReference reference -> add argumentPath reference.Span
        and walkStatements path statements =
            statements
            |> List.iteri (fun index statement ->
                let statementPath = extendPath path (FlowAstPathSegment.BlockStatement index)
                match statement with
                | FlowStatement.Let(_, value, _) -> walkExpression (extendPath statementPath FlowAstPathSegment.LetInitializer) value
                | FlowStatement.LetMany(_, value, _) -> walkExpression (extendPath statementPath FlowAstPathSegment.DestructureInitializer) value
                | FlowStatement.Evaluate expression -> walkExpression (extendPath statementPath FlowAstPathSegment.EvaluateExpression) expression
                | FlowStatement.Return(values, _) ->
                    values |> List.iteri (fun outputIndex value ->
                        walkExpression (extendPath statementPath (FlowAstPathSegment.ReturnOutput outputIndex)) value))
        walkStatements (FlowAstPath.FlowAstPath []) flowWord.Body
        found |> Seq.toList

    let private attachmentCallPathsAtSpan (attachment: ParsedFlowAttachment) (targetSpan: SourceSpan) =
        let found = ResizeArray<FlowAttachmentBodyRole * FlowAstPath>()
        let add role path span =
            if span = targetSpan then found.Add((role, path))
        let rec walkExpression role path expression =
            match expression with
            | FlowExpression.Call(_, arguments, span) ->
                add role path span
                arguments |> List.iteri (fun index argument -> walkArgument role path FlowCallForm.Direct index argument)
            | FlowExpression.RootCall(_, arguments, span) ->
                add role path span
                arguments |> List.iteri (fun index argument -> walkArgument role path FlowCallForm.AbsoluteRoot index argument)
            | FlowExpression.DotCall(receiver, stage, arguments, span) ->
                add role path span
                walkExpression role (extendPath path FlowAstPathSegment.DotReceiver) receiver
                arguments |> List.iteri (fun index argument -> walkArgument role path (FlowCallForm.DotStage stage) index argument)
            | FlowExpression.If(condition, thenStatements, elseStatements, _) ->
                walkExpression role (extendPath path FlowAstPathSegment.IfCondition) condition
                walkStatements role (extendPath path (FlowAstPathSegment.IfThenStatement 0)) thenStatements
                walkStatements role (extendPath path (FlowAstPathSegment.IfElseStatement 0)) elseStatements
            | FlowExpression.Container(_, _, payload, _) ->
                payload |> Option.iter (walkExpression role (extendPath path FlowAstPathSegment.ContainerPayload))
            | FlowExpression.MatchOption(scrutinee, someCase, noneCase, _) ->
                walkExpression role (extendPath path FlowAstPathSegment.OptionScrutinee) scrutinee
                walkStatements role (extendPath path (FlowAstPathSegment.OptionSomeStatement 0)) someCase.Statements
                walkStatements role (extendPath path (FlowAstPathSegment.OptionNoneStatement 0)) noneCase.Statements
            | FlowExpression.MatchResult(scrutinee, okCase, errorCase, _) ->
                walkExpression role (extendPath path FlowAstPathSegment.ResultScrutinee) scrutinee
                walkStatements role (extendPath path (FlowAstPathSegment.ResultOkStatement 0)) okCase.Statements
                walkStatements role (extendPath path (FlowAstPathSegment.ResultErrorStatement 0)) errorCase.Statements
            | FlowExpression.Literal _ | FlowExpression.Local _ -> ()
        and walkArgument role path form index argument =
            let argumentNodePath = extendPath path (argumentPath form index)
            match argument with
            | FlowArgument.Positional expression -> walkExpression role argumentNodePath expression
            | FlowArgument.Named(_, expression, nameSpan) ->
                add role argumentNodePath nameSpan
                walkExpression role argumentNodePath expression
            | FlowArgument.WordReference reference -> add role argumentNodePath reference.Span
        and walkStatements role path statements =
            statements
            |> List.iteri (fun index statement ->
                let statementPath = extendPath path (FlowAstPathSegment.BlockStatement index)
                match statement with
                | FlowStatement.Let(_, value, _) -> walkExpression role (extendPath statementPath FlowAstPathSegment.LetInitializer) value
                | FlowStatement.LetMany(_, value, _) -> walkExpression role (extendPath statementPath FlowAstPathSegment.DestructureInitializer) value
                | FlowStatement.Evaluate expression -> walkExpression role (extendPath statementPath FlowAstPathSegment.EvaluateExpression) expression
                | FlowStatement.Return(values, _) ->
                    values |> List.iteri (fun outputIndex value ->
                        walkExpression role (extendPath statementPath (FlowAstPathSegment.ReturnOutput outputIndex)) value))
        match attachment with
        | ParsedFlowTest test ->
            walkStatements FlowAttachmentBodyRole.Actual (FlowAstPath.FlowAstPath []) test.Body
            match test.Expected with
            | FlowTestExpectation.Expression expression ->
                walkExpression FlowAttachmentBodyRole.ExpectedExpression (FlowAstPath.FlowAstPath []) expression
            | FlowTestExpectation.Literal _ | FlowTestExpectation.RuntimeError _ -> ()
        | ParsedFlowExample example ->
            walkStatements FlowAttachmentBodyRole.Actual (FlowAstPath.FlowAstPath []) example.Body
        found |> Seq.distinct |> Seq.toList

    let private attachmentKindName = function
        | FlowAttachmentKind.Test -> "test"
        | FlowAttachmentKind.Example -> "example"

    let private withAttachmentDiagnosticContext
        (document: FlowAttachmentSourceDocument)
        (attachment: ParsedFlowAttachment option)
        action =
        try action ()
        with
        | LanguageException diagnostic ->
            let locations =
                match attachment, diagnostic.Span with
                | Some parsed, Some span -> attachmentCallPathsAtSpan parsed span
                | _ -> []
            let locationText =
                match locations with
                | [] -> ""
                | _ ->
                    locations
                    |> List.map (fun (role, path) -> sprintf "%A at %A" role path)
                    |> List.distinct
                    |> String.concat "; "
                    |> sprintf " Authored body location(s): %s."
            let priorWordText =
                match diagnostic.Word with
                | Some word when word <> document.OwnerName -> $" Diagnostic target word: '{word}'."
                | _ -> ""
            let contextText =
                $" Flow attachment context: owner='{document.OwnerName}' ({document.OwnerId}), kind='{attachmentKindName document.Kind}', case='{document.CaseName}'."
            let owner =
                if String.IsNullOrWhiteSpace document.OwnerName then diagnostic.Word
                else Some document.OwnerName
            raise (LanguageException { diagnostic with Word = owner; Message = diagnostic.Message + contextText + locationText + priorWordText })

    let private withFlowOwnerPath (flowWord: FlowWordDefinition) action =
        try action ()
        with
        | LanguageException diagnostic ->
            let paths =
                diagnostic.Span
                |> Option.map (flowPathsAtSpan flowWord)
                |> Option.defaultValue []
            let pathText = paths |> List.map (sprintf "%A") |> String.concat ", "
            let message =
                if List.isEmpty paths then diagnostic.Message
                else diagnostic.Message + " Flow AST path(s): " + pathText + "."
            raise (LanguageException { diagnostic with Word = Some flowWord.Name; Message = message })

    let private lowerExpressionBody context expression : FlowLoweredExpression =
        let state = freshState context
        inferExpression context state Map.empty expression |> ignore
        let lowered = lowerFlowExpression context state Map.empty (FlowAstPath.FlowAstPath []) expression
        { Expressions = lowered.Expressions
          SourceText = FlowSource.renderExpression expression
          SyntaxVersion = 1
          Projection = makeProjection state }

    let lowerExpression context expression =
        FlowStructure.validateExpressionNesting [ expression ]
        lowerExpressionBody context expression

    let checkExpression context expression =
        let lowered = lowerExpression context expression
        let checkedExpression = Compiler.checkExpression (knownTypes context) context.CompilerContext.Words lowered.Expressions
        lowered, checkedExpression

    let compileExpression context expression : CompiledExpression =
        let lowered, checkedExpression = checkExpression context expression
        match checkedExpression.Stack with
        | [ _ ] -> ()
        | actual -> fail "FLOW_EXPRESSION_ARITY" "An isolated Flow expression must produce exactly one value." None (Some(spanOfExpression expression)) [ "one value" ] (actual |> List.map Types.format)
        let program = Compiler.compileIrProgramWithSourceOrigins context.CompilerContext context.SourceOrigins
        let origins = mergeOrigins context lowered.Projection
        let body = Compiler.compileIrBodyAgainstProgramWithSourceOrigins context.CompilerContext program "<flow-expression>" [] lowered.Expressions origins
        let bodyData = VerifiedIrBody.inspect body
        { Lowered = lowered
          Program = program
          Body = body
          SiteOrigins = sourceSites bodyData.BodySourceMap }

    type private LoweredWordArtifacts =
        { Lowered: FlowLoweredWord
          CallEvents: FlowCallEvent list }

    let private lowerWordWithSignaturesAndEvents context signatures retainedOrigins (flowWord: FlowWordDefinition) : LoweredWordArtifacts =
        FlowStructure.validateWordNesting flowWord
        if flowWord.SyntaxVersion <> 1 then fail "FLOW_VERSION_UNSUPPORTED" "Only Flow syntax version 1 is supported by this compiler slice." (Some flowWord.Name) (Some flowWord.Span) [ "1" ] [ string flowWord.SyntaxVersion ]
        let names = flowWord.Parameters |> List.map (fun parameter -> parameter.Name)
        if names.Length <> (Set.ofList names).Count then fail "FLOW_DUPLICATE_PARAMETER" "Flow word parameters must have unique names." (Some flowWord.Name) (Some flowWord.Span) [] names
        let state = freshStateWith retainedOrigins signatures
        rememberSpan state flowWord.Span
        let environment =
            flowWord.Parameters
            |> List.mapi (fun index parameter ->
                rememberSpan state parameter.Span
                parameter.Name, { InternalName = internalParameterName index; Type = parameter.Type })
            |> Map.ofList
        let parameterBindings =
            flowWord.Parameters
            |> List.mapi (fun index parameter -> index, parameter)
            |> List.rev
            |> List.map (fun (index, parameter) -> Let(internalParameterName index, syntheticSpan state parameter.Span))
        let body, _ = lowerStatements context state environment (FlowAstPath.FlowAstPath []) flowWord.Body
        let definition =
            { Name = flowWord.Name
              Inputs = flowWord.Parameters |> List.map (fun parameter -> parameter.Type)
              Outputs = flowWord.Outputs
              Effects = flowWord.Effects
              Maturity = ProjectWord
              Revision = 0
              Documentation = flowWord.Documentation
              Body = parameterBindings @ body.Expressions
              SourceText = flowWord.SourceText
              Span = flowWord.Span }
        { Lowered =
            { Definition = definition
              ParameterNames = names
              SourceText = flowWord.SourceText
              SyntaxVersion = flowWord.SyntaxVersion
              Projection = makeProjection state }
          CallEvents = body.CallEvents }

    let private lowerWordWithSignatures context signatures retainedOrigins flowWord =
        (lowerWordWithSignaturesAndEvents context signatures retainedOrigins flowWord).Lowered

    let lowerWord context (flowWord: FlowWordDefinition) : FlowLoweredWord =
        lowerWordWithSignatures context (signatureCatalog context) context.SourceOrigins flowWord

    let checkWord context flowWord =
        let lowered = lowerWord context flowWord
        let checkedDefinition = Compiler.checkDefinition (knownTypes context) context.CompilerContext.Words lowered.Definition
        lowered, checkedDefinition

    let compileWord context wordId flowWord : CompiledWord =
        let lowered, _ = checkWord context flowWord
        if context.CompilerContext.Words.ContainsKey lowered.Definition.Name then
            fail "FLOW_WORD_EXISTS" $"Word '{lowered.Definition.Name}' already exists in the supplied compiler snapshot." (Some lowered.Definition.Name) (Some flowWord.Span) [] [ lowered.Definition.Name ]
        if context.CompilerContext.WordIds.ContainsKey lowered.Definition.Name then
            fail "FLOW_WORD_ID_EXISTS" $"Stable ID for '{lowered.Definition.Name}' already exists in the supplied compiler snapshot." (Some lowered.Definition.Name) (Some flowWord.Span) [] [ lowered.Definition.Name ]
        let entry =
            { Definition = lowered.Definition
              Builtin = None
              Status = Candidate
              Maturity = ProjectWord
              Revision = lowered.Definition.Revision }
        let compilerContext =
            { context.CompilerContext with
                Words = Map.add lowered.Definition.Name entry context.CompilerContext.Words
                WordIds = Map.add lowered.Definition.Name wordId context.CompilerContext.WordIds }
        let combinedOrigins = mergeOrigins context lowered.Projection
        let nextContext =
            { CompilerContext = compilerContext
              ParameterNames = Map.add lowered.Definition.Name lowered.ParameterNames context.ParameterNames
              SourceOrigins = combinedOrigins }
        let program = Compiler.compileIrProgramWithSourceOrigins compilerContext combinedOrigins
        let programData = VerifiedIrProgram.inspect program
        let sites =
            programData.SourceMap
            |> Map.filter (fun _ source -> source.SiteOwner = Some wordId)
            |> sourceSites
        { Lowered = lowered; Context = nextContext; Program = program; SiteOrigins = sites }

    /// Compile a new Flow word and reconcile its transient AST call events with
    /// the verified IR. This result proves the input AST-to-IR correspondence;
    /// it does not claim that the AST came from authenticated persisted bytes.
    let compileWordWithCallBindings (context: Context) (wordId: WordId) (flowWord: FlowWordDefinition) : CompiledCallBoundWord =
        let signatures = signatureCatalog context
        let artifacts = withFlowOwnerPath flowWord (fun () -> lowerWordWithSignaturesAndEvents context signatures context.SourceOrigins flowWord)
        let lowered = artifacts.Lowered
        Compiler.checkDefinition (knownTypes context) context.CompilerContext.Words lowered.Definition |> ignore
        if context.CompilerContext.Words.ContainsKey lowered.Definition.Name then
            fail "FLOW_WORD_EXISTS" $"Word '{lowered.Definition.Name}' already exists in the supplied compiler snapshot." (Some lowered.Definition.Name) (Some flowWord.Span) [] [ lowered.Definition.Name ]
        if context.CompilerContext.WordIds.ContainsKey lowered.Definition.Name then
            fail "FLOW_WORD_ID_EXISTS" $"Stable ID for '{lowered.Definition.Name}' already exists in the supplied compiler snapshot." (Some lowered.Definition.Name) (Some flowWord.Span) [] [ lowered.Definition.Name ]
        let rawWordId = match wordId with | WordId value -> value
        if String.IsNullOrWhiteSpace rawWordId then
            fail "FLOW_WORD_ID_INVALID" "A compiled Flow word requires a nonempty stable identity." (Some lowered.Definition.Name) (Some flowWord.Span) [ "nonempty WordId" ] [ rawWordId ]
        if context.CompilerContext.WordIds |> Map.exists (fun _ existing -> existing = wordId) then
            fail "FLOW_WORD_ID_COLLISION" "A compiled Flow word must use a stable identity not assigned to another dictionary word." (Some lowered.Definition.Name) (Some flowWord.Span) [ "unused WordId" ] [ rawWordId ]
        let entry =
            { Definition = lowered.Definition
              Builtin = None
              Status = Candidate
              Maturity = ProjectWord
              Revision = lowered.Definition.Revision }
        let compilerContext =
            { context.CompilerContext with
                Words = Map.add lowered.Definition.Name entry context.CompilerContext.Words
                WordIds = Map.add lowered.Definition.Name wordId context.CompilerContext.WordIds }
        let combinedOrigins = mergeOrigins context lowered.Projection
        let nextContext =
            { CompilerContext = compilerContext
              ParameterNames = Map.add lowered.Definition.Name lowered.ParameterNames context.ParameterNames
              SourceOrigins = combinedOrigins }
        let program = Compiler.compileIrProgramWithSourceOrigins compilerContext combinedOrigins
        let callSites = reconcileCallEvents program lowered.Definition.Name wordId lowered.Definition.Revision artifacts.CallEvents
        let programData = VerifiedIrProgram.inspect program
        let sites =
            programData.SourceMap
            |> Map.filter (fun _ source -> source.SiteOwner = Some wordId)
            |> sourceSites
        { Lowered = lowered
          Context = nextContext
          Program = program
          SiteOrigins = sites
          CallSites = callSites }

    let private validateBaseFlowSource (context: Context) (baseProgram: VerifiedIrProgram) (document: FlowSourceDocument) (flowWord: FlowWordDefinition) : RetainedFlowSource =
        let entry =
            context.CompilerContext.Words.TryFind document.OwnerName
            |> Option.defaultWith (fun () -> fail "FLOW_SOURCE_OWNER_MISSING" "A declared Flow source owner is absent from the base dictionary." (Some document.OwnerName) (Some flowWord.Span) [] [])
        let identity =
            context.CompilerContext.WordIds.TryFind document.OwnerName
            |> Option.defaultWith (fun () -> fail "FLOW_SOURCE_OWNER_ID_MISSING" "A declared Flow source owner has no base stable ID." (Some document.OwnerName) (Some flowWord.Span) [] [])
        if identity <> document.OwnerId then
            fail "FLOW_SOURCE_OWNER_ID_MISMATCH" "The host-declared Flow owner ID differs from the base dictionary identity." (Some document.OwnerName) (Some flowWord.Span)
                [ sprintf "%A" identity ] [ sprintf "%A" document.OwnerId ]
        if entry.Builtin.IsSome || entry.Status = Primitive then
            fail "FLOW_SOURCE_OWNER_PROTECTED" "A Flow source inventory may contain only user-authored dictionary words." (Some document.OwnerName) (Some flowWord.Span) [ "user word" ] [ document.OwnerName ]
        if entry.Revision <> document.OwnerRevision || entry.Definition.Revision <> document.OwnerRevision then
            fail "FLOW_SOURCE_OWNER_REVISION_MISMATCH" "The host-declared Flow source revision differs from the synchronized base word revision." (Some document.OwnerName) (Some flowWord.Span)
                [ string entry.Revision ] [ string document.OwnerRevision ]
        if entry.Definition.SourceText <> document.Content then
            fail "FLOW_SOURCE_TEXT_MISMATCH" "The exact Flow source bytes differ from the source text retained by the base word definition." (Some document.OwnerName) (Some flowWord.Span)
                [ "exact retained source text" ] [ "different source text" ]
        if entry.Definition.Span.File <> document.SourceFile then
            fail "FLOW_SOURCE_FILE_MISMATCH" "The diagnostic source label must match the base definition so byte-identical source reparses to the exact retained spans." (Some document.OwnerName) (Some flowWord.Span)
                [ entry.Definition.Span.File ] [ document.SourceFile ]
        let artifacts =
            withFlowOwnerPath flowWord (fun () ->
                lowerWordWithSignaturesAndEvents context (signatureCatalog context) context.SourceOrigins flowWord)
        let lowered = artifacts.Lowered
        let actual = entry.Definition
        if lowered.Definition.Name <> actual.Name
           || lowered.Definition.Inputs <> actual.Inputs
           || lowered.Definition.Outputs <> actual.Outputs
           || lowered.Definition.Effects <> actual.Effects
           || lowered.Definition.Documentation <> actual.Documentation
           || semanticExpressions lowered.Definition.Body <> semanticExpressions actual.Body then
            fail "FLOW_SOURCE_BODY_MISMATCH" "The parsed Flow source does not reproduce the base signature, documentation, effects, or semantic body." (Some document.OwnerName) (Some flowWord.Span)
                [ "exact base Flow semantics" ] [ "source differs from base definition" ]
        match entryParameterNames context document.OwnerName entry with
        | Some names when names = lowered.ParameterNames -> ()
        | Some names ->
            fail "FLOW_SOURCE_PARAMETER_METADATA_MISMATCH" "The parsed Flow parameter names differ from the exact base parameter catalog." (Some document.OwnerName) (Some flowWord.Span)
                names lowered.ParameterNames
        | None ->
            fail "FLOW_SOURCE_PARAMETER_METADATA_MISSING" "A source-backed Flow owner requires retained named-parameter metadata." (Some document.OwnerName) (Some flowWord.Span)
                [ "parameter names including an empty list" ] []
        let bindings = reconcileCallEvents baseProgram document.OwnerName document.OwnerId document.OwnerRevision artifacts.CallEvents
        { Document = document
          Definition = flowWord
          BaseBindings = bindings }

    let private validateFlowSourceInventory (context: Context) (inventory: FlowSourceInventory) : RetainedFlowSource list =
        let ownerIds = inventory.Sources |> List.map (fun document -> document.OwnerId)
        let ownerNames = inventory.Sources |> List.map (fun document -> document.OwnerName)
        let duplicateIds = ownerIds |> List.groupBy id |> List.tryFind (fun (_, values) -> values.Length > 1)
        match duplicateIds with
        | Some(identity, _) ->
            fail "FLOW_SOURCE_INVENTORY_DUPLICATE_ID" "A Flow source inventory may contain at most one document per stable owner ID." None None [] [ sprintf "%A" identity ]
        | None -> ()
        let duplicateNames = ownerNames |> List.groupBy id |> List.tryFind (fun (_, values) -> values.Length > 1)
        match duplicateNames with
        | Some(name, _) -> fail "FLOW_SOURCE_INVENTORY_DUPLICATE_NAME" "A Flow source inventory may contain at most one document per owner name." (Some name) None [] [ name ]
        | None -> ()
        let actualIds = Set.ofList ownerIds
        let missingIds = Set.difference inventory.ExpectedFlowOwnerIds actualIds
        let undeclaredIds = Set.difference actualIds inventory.ExpectedFlowOwnerIds
        if not (Set.isEmpty missingIds) || not (Set.isEmpty undeclaredIds) then
            fail "FLOW_SOURCE_INVENTORY_INCOMPLETE" "The Flow source inventory must exactly cover the host-declared current Flow owner IDs." None None
                (inventory.ExpectedFlowOwnerIds |> Set.toList |> List.map string)
                ([ yield! missingIds |> Set.toList |> List.map (sprintf "missing %A")
                   yield! undeclaredIds |> Set.toList |> List.map (sprintf "undeclared %A") ])
        let namesById =
            context.CompilerContext.WordIds
            |> Map.toList
            |> List.map (fun (name, identity) -> identity, name)
            |> Map.ofList
        for identity in inventory.ExpectedFlowOwnerIds do
            if not (namesById.ContainsKey identity) then
                fail "FLOW_SOURCE_INVENTORY_OWNER_MISSING" "A host-declared Flow owner ID is absent from the base dictionary." None None [ sprintf "%A" identity ] []
        let parsed =
            inventory.Sources
            |> List.map (fun document -> document, validateFlowSourceDocument document)
        // Verify the supplied base snapshot independently. This baseline check
        // is distinct from the one final compile of all proposed real bodies.
        let baseProgram = Compiler.compileIrProgramWithSourceOrigins context.CompilerContext context.SourceOrigins
        parsed
        |> List.map (fun (document, definition) ->
            match namesById.TryFind document.OwnerId with
            | Some ownerName when ownerName = document.OwnerName -> ()
            | Some ownerName ->
                fail "FLOW_SOURCE_OWNER_NAME_MISMATCH" "The host-declared owner name differs from the base ID-to-name catalog." (Some document.OwnerName) (Some definition.Span)
                    [ ownerName ] [ document.OwnerName ]
            | None ->
                fail "FLOW_SOURCE_OWNER_ID_MISSING" "A Flow source document refers to an ID that is not assigned in the base dictionary." (Some document.OwnerName) (Some definition.Span)
                    [ "base owner ID" ] [ sprintf "%A" document.OwnerId ]
            validateBaseFlowSource context baseProgram document definition)
        |> List.sortBy (fun retained -> retained.Document.OwnerName)

    let private checkRetainedBindingStability ownerName (baseline: FlowCallSite list) (proposed: FlowCallSite list) =
        let baselineByPath = baseline |> List.map (fun site -> site.Path, site) |> Map.ofList
        let proposedByPath = proposed |> List.map (fun site -> site.Path, site) |> Map.ofList
        let baselinePaths = baselineByPath |> Map.toSeq |> Seq.map fst |> Set.ofSeq
        let proposedPaths = proposedByPath |> Map.toSeq |> Seq.map fst |> Set.ofSeq
        let removedPaths = Set.difference baselinePaths proposedPaths
        let addedPaths = Set.difference proposedPaths baselinePaths
        if not (Set.isEmpty removedPaths) || not (Set.isEmpty addedPaths) then
            let path, span =
                match addedPaths |> Set.toList |> List.tryHead with
                | Some path -> path, proposedByPath[path].Span
                | None ->
                    let path = removedPaths |> Set.toList |> List.head
                    path, baselineByPath[path].Span
            fail "FLOW_CALL_BINDING_SITE_SET_CHANGED" "Re-lowering unchanged Flow source changed its structural call-site set." (Some ownerName) (Some span)
                (baselinePaths |> Set.toList |> List.map (sprintf "%A"))
                (proposedPaths |> Set.toList |> List.map (sprintf "%A"))
        for path in baselinePaths do
            let previous = baselineByPath[path]
            let current = proposedByPath[path]
            if previous.Target <> current.Target then
                fail "FLOW_CALL_REBOUND" "An unchanged authored Flow call now resolves to a different stable target identity." (Some ownerName) (Some current.Span)
                    [ sprintf "%A at %A" previous.Target path ] [ sprintf "%A at %A" current.Target path ]

    let private zeroWidthMarkers (expressions: Expr list) =
        let rec collect body =
            body
            |> List.fold (fun found expression ->
                let span =
                    match expression with
                    | Push(_, span) | Call(_, span) | ConstructContainer(_, _, span)
                    | MapList(_, span) | FilterList(_, span) | EachList(_, span)
                    | Let(_, span) | Load(_, span) | If(_, _, span) | Scope(_, span)
                    | MatchOption(_, _, _, span) | MatchResult(_, _, _, _, span) -> span
                let found = if span.Length = 0 then Set.add span found else found
                match expression with
                | If(thenBranch, elseBranch, _) -> Set.union found (Set.union (collect thenBranch) (collect elseBranch))
                | Scope(innerBody, _) -> Set.union found (collect innerBody)
                | MatchOption(_, someBranch, noneBranch, _) -> Set.union found (Set.union (collect someBranch) (collect noneBranch))
                | MatchResult(_, _, okBranch, errorBranch, _) -> Set.union found (Set.union (collect okBranch) (collect errorBranch))
                | _ -> found) Set.empty
        collect expressions

    let private contextZeroWidthMarkers (compilerContext: Compiler.IrLoweringContext) =
        compilerContext.Words
        |> Map.toSeq
        |> Seq.fold (fun found (_, entry) -> Set.union found (zeroWidthMarkers entry.Definition.Body)) Set.empty

    let private requireExactBatchOrigins (context: Context) =
        let markers = contextZeroWidthMarkers context.CompilerContext
        let origins = context.SourceOrigins |> Map.toSeq |> Seq.map fst |> Set.ofSeq
        let missing = Set.difference markers origins
        let extra = Set.difference origins markers
        if not (Set.isEmpty missing) then
            fail "IR_SOURCE_ORIGIN_MISSING" "The base compiler snapshot has Flow source markers without authored origins." None None
                [ "one source origin per zero-width marker" ] [ sprintf "missing marker count=%d" missing.Count ]
        if not (Set.isEmpty extra) then
            fail "IR_SOURCE_ORIGIN_SET_MISMATCH" "The base source-origin snapshot contains stale markers." None None
                [ "no stale source markers" ] [ sprintf "extra marker count=%d" extra.Count ]
        for KeyValue(marker, origin) in context.SourceOrigins do
            if marker.Length <> 0 || origin.Length <= 0 || String.IsNullOrWhiteSpace origin.File || origin.Line < 1 || origin.Column < 1 then
                fail "IR_SOURCE_ORIGIN_INVALID" "Base source-origin overrides must map a zero-width private marker to a valid authored span." None None
                    [ "private marker -> authored source span" ] [ "invalid mapping" ]

    let private validateBatchType (knownTypes: Set<string>) (word: string) (span: SourceSpan) (allowVariables: bool) (typeValue: LangType) =
        let rec visit (depth: int) (typeValue: LangType) =
            match typeValue with
            | _ when depth > FlowStructure.maxExpressionDepth ->
                fail "FLOW_NESTING_LIMIT" "Batch signature type nesting exceeds the Flow structural limit." (Some word) (Some span)
                    [ string FlowStructure.maxExpressionDepth ] [ string depth ]
            | TInt | TFloat | TBool | TString | TUnit -> ()
            | TList item | TOption item -> visit (depth + 1) item
            | TResult(okType, errorType) -> visit (depth + 1) okType; visit (depth + 1) errorType
            | TNamed name when knownTypes.Contains name -> ()
            | TNamed name -> fail "FLOW_BATCH_UNKNOWN_TYPE" $"Batch word '{word}' refers to undeclared type '{name}'." (Some word) (Some span) [ "declared record or scalar" ] [ name ]
            | TVar name when allowVariables -> ()
            | TVar name -> fail "FLOW_BATCH_OPEN_TYPE" $"Batch word '{word}' uses type variable '{name}', which is only permitted in trusted primitive signatures." (Some word) (Some span) [ "closed source type" ] [ name ]
        visit 0 typeValue

    let private validParameterName (name: string) =
        not (String.IsNullOrEmpty name)
        && (Char.IsLetter name[0] || name[0] = '_')
        && (name |> Seq.skip 1 |> Seq.forall (fun character -> Char.IsLetterOrDigit character || character = '_' || character = '-' || character = '?' || character = '!'))

    let private validateParameterNames (word: string) (span: SourceSpan) (inputCount: int) (parameterNames: string list) =
        if parameterNames.Length <> inputCount then
            fail "FLOW_BATCH_PARAMETER_ARITY" $"Parameter names for '{word}' do not match its input signature." (Some word) (Some span)
                [ string inputCount ] [ string parameterNames.Length ]
        if parameterNames |> List.exists (validParameterName >> not) then
            fail "FLOW_BATCH_PARAMETER_NAME" $"Parameter catalog for '{word}' contains an invalid name." (Some word) (Some span)
                [ "valid identifier names" ] parameterNames
        let duplicates = parameterNames |> List.groupBy id |> List.tryFind (fun (_, values) -> values.Length > 1)
        match duplicates with
        | Some(name, _) -> fail "FLOW_BATCH_PARAMETER_DUPLICATE" $"Parameter catalog for '{word}' repeats '{name}'." (Some word) (Some span) [ "unique parameter names" ] [ name ]
        | None -> ()

    let private validateContextCatalog (context: Context) =
        let compilerContext = context.CompilerContext
        let wordNames = compilerContext.Words |> Map.toSeq |> Seq.map fst |> Set.ofSeq
        let idNames = compilerContext.WordIds |> Map.toSeq |> Seq.map fst |> Set.ofSeq
        if wordNames <> idNames then
            fail "FLOW_BATCH_BASE_ID_MAP" "The base word dictionary and stable-ID catalog must have identical keys." None None
                (wordNames |> Set.toList) (idNames |> Set.toList)
        let ids = compilerContext.WordIds |> Map.toList |> List.map snd
        if ids |> List.exists (function WordId raw -> String.IsNullOrWhiteSpace raw) then
            fail "FLOW_BATCH_BASE_ID_INVALID" "Every base word must have a nonempty stable identity." None None [ "nonempty WordId values" ] [ "empty identity" ]
        let duplicateIds = ids |> List.groupBy id |> List.tryFind (fun (_, entries) -> entries.Length > 1)
        match duplicateIds with
        | Some(wordId, _) -> fail "FLOW_BATCH_BASE_ID_DUPLICATE" "Base dictionary words must have globally unique stable identities." None None [ "unique WordId values" ] [ sprintf "%A" wordId ]
        | None -> ()
        let unknownParameterKeys = context.ParameterNames |> Map.toSeq |> Seq.map fst |> Set.ofSeq |> Set.difference <| wordNames
        if not (Set.isEmpty unknownParameterKeys) then
            fail "FLOW_BATCH_PARAMETER_CATALOG_KEY" "Parameter metadata must refer only to words in the base dictionary." None None
                (wordNames |> Set.toList) (unknownParameterKeys |> Set.toList)
        let known = knownTypes context
        let recordNames = compilerContext.Records |> Map.toSeq |> Seq.map fst |> Set.ofSeq
        let scalarNames = compilerContext.Scalars |> Map.toSeq |> Seq.map fst |> Set.ofSeq
        let typeCollision = Set.intersect recordNames scalarNames
        if not (Set.isEmpty typeCollision) then
            fail "FLOW_BATCH_TYPE_CATALOG_COLLISION" "Record and scalar type names must be globally unique." None None [] (typeCollision |> Set.toList)
        for KeyValue(name, record) in compilerContext.Records do
            if record.Name <> name then fail "FLOW_BATCH_TYPE_CATALOG_KEY" "Record map keys must match their declared names." (Some name) (Some record.Span) [ name ] [ record.Name ]
            for (field: RecordField) in record.Fields do validateBatchType known name record.Span false field.Type
        for KeyValue(name, scalar) in compilerContext.Scalars do
            if scalar.Name <> name then fail "FLOW_BATCH_TYPE_CATALOG_KEY" "Scalar map keys must match their declared names." (Some name) (Some scalar.Span) [ name ] [ scalar.Name ]
            validateBatchType known name scalar.Span false scalar.BaseType
        for KeyValue(name, entry) in compilerContext.Words do
            if entry.Definition.Name <> name then fail "FLOW_BATCH_WORD_CATALOG_KEY" "Word map keys must match their declared names." (Some name) (Some entry.Definition.Span) [ name ] [ entry.Definition.Name ]
            if entry.Revision < 0 || entry.Definition.Revision <> entry.Revision then
                fail "FLOW_BATCH_BASE_REVISION" "Base word and definition revisions must be equal and nonnegative." (Some name) (Some entry.Definition.Span)
                    [ string entry.Revision ] [ string entry.Definition.Revision ]
            for effect in entry.Definition.Effects do
                if IrEffects.ofName effect |> Option.isNone then
                    fail "FLOW_BATCH_UNKNOWN_EFFECT" $"Base word '{name}' declares an effect outside the closed effect vocabulary." (Some name) (Some entry.Definition.Span) (IrEffects.names Set.empty) [ effect ]
            let isPrimitive = match entry.Builtin with | Some(BuiltinOp _) -> true | _ -> false
            for typeValue in entry.Definition.Inputs @ entry.Definition.Outputs do
                validateBatchType known name entry.Definition.Span isPrimitive typeValue
            match entryParameterNames context name entry with
            | Some names -> validateParameterNames name entry.Definition.Span entry.Definition.Inputs.Length names
            | None -> ()
            match context.ParameterNames.TryFind name with
            | Some names -> validateParameterNames name entry.Definition.Span entry.Definition.Inputs.Length names
            | None -> ()
        requireExactBatchOrigins context

    let private validateProposedSignature (context: Context) (flowWord: FlowWordDefinition) =
        FlowStructure.validateWordNesting flowWord
        let nameSegments = if String.IsNullOrEmpty flowWord.Name then [||] else flowWord.Name.Split('.')
        if nameSegments.Length = 0 || (nameSegments |> Array.exists (validParameterName >> not)) then
            fail "FLOW_BATCH_WORD_NAME_INVALID" "A batch Flow word name must be a canonical dotted dictionary name." (Some flowWord.Name) (Some flowWord.Span)
                [ "identifier(.identifier)*" ] [ flowWord.Name ]
        let names = flowWord.Parameters |> List.map (fun parameter -> parameter.Name)
        validateParameterNames flowWord.Name flowWord.Span flowWord.Parameters.Length names
        let known = knownTypes context
        for parameter in flowWord.Parameters do validateBatchType known flowWord.Name parameter.Span false parameter.Type
        for output in flowWord.Outputs do validateBatchType known flowWord.Name flowWord.Span false output
        for effect in flowWord.Effects do
            if IrEffects.ofName effect |> Option.isNone then
                fail "FLOW_BATCH_UNKNOWN_EFFECT" $"Batch word '{flowWord.Name}' declares an effect outside the closed effect vocabulary." (Some flowWord.Name) (Some flowWord.Span) (IrEffects.names Set.empty) [ effect ]

    type private PreparedBatchChange =
        { Definition: FlowWordDefinition
          Identity: WordId
          Revision: int
          Previous: WordEntry option }

    type private PreparedLoweredBatchChange =
        { Prepared: PreparedBatchChange
          Artifacts: LoweredWordArtifacts
          Source: FlowSourceDocument option }

    let private compileBatchWordsCore
        (context: Context)
        (changes: FlowWordChange list)
        (sourceDocuments: Map<string, FlowSourceDocument>)
        (retainedSources: RetainedFlowSource list)
        : BatchCompilationArtifacts =
        validateContextCatalog context
        // Validate every intent against the immutable input before creating any
        // overlay map, so duplicate or stale requests cannot partly mutate state.
        let prepared = ResizeArray<PreparedBatchChange>()
        let changedNames = System.Collections.Generic.HashSet<string>(StringComparer.Ordinal)
        let addedIds = System.Collections.Generic.HashSet<WordId>()
        let baseIds = context.CompilerContext.WordIds |> Map.toSeq |> Seq.map snd |> Set.ofSeq
        for change in changes do
            let flowWord = change.Definition
            validateProposedSignature context flowWord
            if not (changedNames.Add flowWord.Name) then
                fail "FLOW_BATCH_DUPLICATE_NAME" "A word name may appear only once in one batch." (Some flowWord.Name) (Some flowWord.Span) [ "one change per word name" ] [ flowWord.Name ]
            match change.RevisionIntent with
            | FlowWordRevisionIntent.Add(identity, revision) ->
                if context.CompilerContext.Words.ContainsKey flowWord.Name || context.CompilerContext.WordIds.ContainsKey flowWord.Name then
                    fail "FLOW_BATCH_ADD_EXISTS" $"Cannot add '{flowWord.Name}' because that word already exists." (Some flowWord.Name) (Some flowWord.Span) [ "unused dictionary name" ] [ flowWord.Name ]
                let rawIdentity = match identity with | WordId value -> value
                if String.IsNullOrWhiteSpace rawIdentity then fail "FLOW_BATCH_ID_INVALID" "New words require a nonempty stable ID." (Some flowWord.Name) (Some flowWord.Span) [ "nonempty WordId" ] [ rawIdentity ]
                if revision < 0 then fail "FLOW_BATCH_REVISION_INVALID" "New word revisions must be nonnegative." (Some flowWord.Name) (Some flowWord.Span) [ "nonnegative revision" ] [ string revision ]
                if baseIds.Contains identity || not (addedIds.Add identity) then
                    fail "FLOW_BATCH_ID_COLLISION" "A new word must use a stable ID not assigned to any base or other added word." (Some flowWord.Name) (Some flowWord.Span) [ "unused WordId" ] [ rawIdentity ]
                prepared.Add { Definition = flowWord; Identity = identity; Revision = revision; Previous = None }
            | FlowWordRevisionIntent.Replace(identity, expectedRevision, revision) ->
                let previous =
                    context.CompilerContext.Words.TryFind flowWord.Name
                    |> Option.defaultWith (fun () -> fail "FLOW_BATCH_REPLACE_MISSING" $"Cannot replace missing word '{flowWord.Name}'." (Some flowWord.Name) (Some flowWord.Span) [ "existing user word" ] [])
                let expectedIdentity = context.CompilerContext.WordIds[flowWord.Name]
                if previous.Builtin.IsSome || previous.Status = Primitive then
                    fail "FLOW_BATCH_REPLACE_PROTECTED" $"Built-in or generated word '{flowWord.Name}' cannot be replaced by Flow." (Some flowWord.Name) (Some flowWord.Span) [ "user-defined word" ] [ flowWord.Name ]
                if identity <> expectedIdentity then
                    fail "FLOW_BATCH_REPLACE_ID_MISMATCH" "Replacing a word must preserve its stable identity." (Some flowWord.Name) (Some flowWord.Span) [ sprintf "%A" expectedIdentity ] [ sprintf "%A" identity ]
                if expectedRevision <> previous.Revision then
                    fail "FLOW_BATCH_STALE_REVISION" "Replacement intent does not match the current word revision." (Some flowWord.Name) (Some flowWord.Span) [ string previous.Revision ] [ string expectedRevision ]
                if revision <= expectedRevision then
                    fail "FLOW_BATCH_REVISION_NOT_ADVANCED" "Replacement revision must advance beyond the revision it replaces." (Some flowWord.Name) (Some flowWord.Span) [ $"> {expectedRevision}" ] [ string revision ]
                prepared.Add { Definition = flowWord; Identity = identity; Revision = revision; Previous = Some previous }

        let baseSignatures = signatureCatalog context
        let signatureOverlay =
            prepared
            |> Seq.fold (fun signatures item ->
                let flowWord = item.Definition
                let candidate =
                    { Name = flowWord.Name
                      Inputs = flowWord.Parameters |> List.map (fun parameter -> parameter.Type)
                      Outputs = flowWord.Outputs
                      Effects = flowWord.Effects
                      Kind = SignatureKind.UserWord
                      ParameterNames = Some(flowWord.Parameters |> List.map (fun parameter -> parameter.Name))
                      Identity = Some item.Identity
                      Revision = item.Revision
                      Span = flowWord.Span }
                Map.add flowWord.Name candidate signatures) baseSignatures

        let changedNameSet = prepared |> Seq.map (fun item -> item.Definition.Name) |> Set.ofSeq
        let retainedNameSet = retainedSources |> List.map (fun source -> source.Document.OwnerName) |> Set.ofList
        let retainedDuplicates = retainedSources |> List.groupBy (fun source -> source.Document.OwnerName) |> List.tryFind (fun (_, values) -> values.Length > 1)
        match retainedDuplicates with
        | Some(name, _) -> fail "FLOW_SOURCE_INVENTORY_DUPLICATE_NAME" "A retained Flow source owner may appear only once." (Some name) None [] [ name ]
        | None -> ()
        let changedRetainedCollision = Set.intersect changedNameSet retainedNameSet
        if not (Set.isEmpty changedRetainedCollision) then
            fail "FLOW_SOURCE_INVENTORY_CHANGED_OWNER_RETAINED" "A Flow owner cannot be both changed and retained in one source-backed batch." None None [] (changedRetainedCollision |> Set.toList)
        if not (Map.isEmpty sourceDocuments) then
            let expectedSourceNames = changedNameSet
            let actualSourceNames = sourceDocuments |> Map.toSeq |> Seq.map fst |> Set.ofSeq
            if expectedSourceNames <> actualSourceNames then
                fail "FLOW_SOURCE_CHANGE_COVERAGE" "Every source-backed word change must have exactly one parsed source document." None None
                    (expectedSourceNames |> Set.toList) (actualSourceNames |> Set.toList)

        let mutable allocationOrigins = context.SourceOrigins
        let loweredChangeArtifacts =
            prepared
            |> Seq.map (fun item ->
                let loweredArtifacts =
                    withFlowOwnerPath item.Definition (fun () ->
                        lowerWordWithSignaturesAndEvents context signatureOverlay allocationOrigins item.Definition)
                let lowered = loweredArtifacts.Lowered
                let collision = lowered.Projection.SyntheticOrigins |> Map.toSeq |> Seq.tryFind (fun (marker, _) -> allocationOrigins.ContainsKey marker)
                match collision with
                | Some(marker, _) -> fail "FLOW_SOURCE_ORIGIN_COLLISION" "Batch Flow source markers must remain unique across words." (Some item.Definition.Name) (Some item.Definition.Span) [] [ sprintf "%A" marker ]
                | None -> ()
                allocationOrigins <- Map.fold (fun found marker origin -> Map.add marker origin found) allocationOrigins lowered.Projection.SyntheticOrigins
                let definition =
                    match item.Previous with
                    | Some previous -> { lowered.Definition with Revision = item.Revision; Maturity = previous.Maturity }
                    | None -> { lowered.Definition with Revision = item.Revision }
                let lowered = { lowered with Definition = definition }
                let source = sourceDocuments.TryFind item.Definition.Name
                match source with
                | Some document when document.OwnerId <> item.Identity || document.OwnerRevision <> item.Revision ->
                    fail "FLOW_SOURCE_CHANGE_REVISION_MISMATCH" "A changed source owner ID/revision differs from its validated batch revision intent." (Some item.Definition.Name) (Some item.Definition.Span)
                        [ sprintf "%A@%d" item.Identity item.Revision ] [ sprintf "%A@%d" document.OwnerId document.OwnerRevision ]
                | _ -> ()
                { Prepared = item
                  Artifacts = { loweredArtifacts with Lowered = lowered }
                  Source = source })
            |> Seq.toList

        let loweredRetainedArtifacts =
            retainedSources
            |> List.sortBy (fun source -> source.Document.OwnerName)
            |> List.map (fun retained ->
                let ownerName = retained.Document.OwnerName
                let previous =
                    context.CompilerContext.Words.TryFind ownerName
                    |> Option.defaultWith (fun () -> fail "FLOW_SOURCE_OWNER_MISSING" "A retained Flow source owner disappeared from the base context." (Some ownerName) None [] [])
                let ownerId =
                    context.CompilerContext.WordIds.TryFind ownerName
                    |> Option.defaultWith (fun () -> fail "FLOW_SOURCE_OWNER_ID_MISSING" "A retained Flow source owner has no stable ID in the base context." (Some ownerName) None [] [])
                let loweredArtifacts =
                    withFlowOwnerPath retained.Definition (fun () ->
                        lowerWordWithSignaturesAndEvents context signatureOverlay allocationOrigins retained.Definition)
                let lowered = loweredArtifacts.Lowered
                let collision = lowered.Projection.SyntheticOrigins |> Map.toSeq |> Seq.tryFind (fun (marker, _) -> allocationOrigins.ContainsKey marker)
                match collision with
                | Some(marker, _) -> fail "FLOW_SOURCE_ORIGIN_COLLISION" "Re-lowered retained Flow source markers must remain unique across the batch." (Some ownerName) (Some retained.Definition.Span) [] [ sprintf "%A" marker ]
                | None -> ()
                allocationOrigins <- Map.fold (fun found marker origin -> Map.add marker origin found) allocationOrigins lowered.Projection.SyntheticOrigins
                let lowered =
                    { lowered with
                        Definition =
                            { lowered.Definition with
                                Revision = previous.Revision
                                Maturity = previous.Maturity } }
                let loweredArtifacts = { loweredArtifacts with Lowered = lowered }
                let proposedBindings = loweredArtifacts.CallEvents |> List.map (callSiteOfEvent ownerName)
                checkRetainedBindingStability ownerName retained.BaseBindings proposedBindings
                { Document = Some retained.Document
                  OwnerName = ownerName
                  OwnerId = ownerId
                  OwnerRevision = previous.Revision
                  Lowered = lowered
                  CallEvents = loweredArtifacts.CallEvents })

        let loweredChanges = loweredChangeArtifacts |> List.map (fun item -> item.Artifacts.Lowered)
        let finalWords =
            loweredChangeArtifacts
            |> List.fold (fun words change ->
                let lowered = change.Artifacts.Lowered
                let name = lowered.Definition.Name
                let entry =
                    match change.Prepared.Previous with
                    | Some previous ->
                        { previous with
                            Definition = lowered.Definition
                            Revision = change.Prepared.Revision }
                    | None ->
                        { Definition = lowered.Definition
                          Builtin = None
                          Status = Candidate
                          Maturity = ProjectWord
                          Revision = change.Prepared.Revision }
                Map.add name entry words) context.CompilerContext.Words
        let finalWords =
            loweredRetainedArtifacts
            |> List.fold (fun (words: Map<string, WordEntry>) (retained: AuthoredLoweredOwner) ->
                let previous = words[retained.OwnerName]
                Map.add retained.OwnerName { previous with Definition = retained.Lowered.Definition; Revision = retained.OwnerRevision } words) finalWords
        let finalIds =
            prepared
            |> Seq.fold (fun ids item -> Map.add item.Definition.Name item.Identity ids) context.CompilerContext.WordIds
        let finalCompilerContext =
            { context.CompilerContext with
                Words = finalWords
                WordIds = finalIds }
        let finalParameterNames =
            (loweredChanges @ (loweredRetainedArtifacts |> List.map (fun item -> item.Lowered)))
            |> List.fold (fun parameters lowered -> Map.add lowered.Definition.Name lowered.ParameterNames parameters) context.ParameterNames
        let finalMarkers = contextZeroWidthMarkers finalCompilerContext
        let retainedOrigins = allocationOrigins |> Map.filter (fun marker _ -> finalMarkers.Contains marker)
        let retainedKeys = retainedOrigins |> Map.toSeq |> Seq.map fst |> Set.ofSeq
        let missingFinalOrigins = Set.difference finalMarkers retainedKeys
        if not (Set.isEmpty missingFinalOrigins) then
            fail "IR_SOURCE_ORIGIN_MISSING" "Final batch words contain a private Flow marker without an authored source origin." None None
                [ "one source origin per final marker" ] [ sprintf "missing marker count=%d" missingFinalOrigins.Count ]
        let finalContext =
            { CompilerContext = finalCompilerContext
              ParameterNames = finalParameterNames
              SourceOrigins = retainedOrigins }
        // This is the single authoritative check of all real bodies. It sees
        // forward references, final effects, scalar validators and call cycles.
        let program = Compiler.compileIrProgramWithSourceOrigins finalCompilerContext retainedOrigins
        let programData = VerifiedIrProgram.inspect program
        let changedIds = prepared |> Seq.map (fun item -> item.Identity) |> Set.ofSeq
        let sites =
            programData.SourceMap
            |> Map.filter (fun _ source -> source.SiteOwner |> Option.exists changedIds.Contains)
            |> sourceSites
        let authoredChangedOwners =
            loweredChangeArtifacts
            |> List.choose (fun item ->
                item.Source
                |> Option.map (fun document ->
                    { Document = Some document
                      OwnerName = document.OwnerName
                      OwnerId = document.OwnerId
                      OwnerRevision = document.OwnerRevision
                      Lowered = item.Artifacts.Lowered
                      CallEvents = item.Artifacts.CallEvents }))
        let authoredOwners = (authoredChangedOwners @ loweredRetainedArtifacts) |> List.sortBy (fun item -> item.OwnerName)
        { Compilation =
            { LoweredWords = loweredChanges
              Context = finalContext
              Program = program
              SiteOrigins = sites }
          AuthoredOwners = authoredOwners }

    let compileBatchWords (context: Context) (changes: FlowWordChange list) : FlowBatchCompilation =
        if List.isEmpty changes then fail "FLOW_BATCH_EMPTY" "A Flow word batch must contain at least one change." None None [ "one or more word changes" ] []
        (compileBatchWordsCore context changes Map.empty [] ).Compilation

    let private compileBatchFlowSourcesCore
        allowEmpty
        (context: Context)
        (inventory: FlowSourceInventory)
        (sourceChanges: FlowSourceChange list)
        : FlowBoundBatchCompilation =
        validateContextCatalog context
        if List.isEmpty sourceChanges && not allowEmpty then
            fail "FLOW_BATCH_EMPTY" "A source-backed Flow batch must contain at least one word change." None None [ "one or more source changes" ] []
        let retainedBase = validateFlowSourceInventory context inventory
        let changedSourceNames = sourceChanges |> List.map (fun change -> change.Source.OwnerName)
        let changedSourceIds = sourceChanges |> List.map (fun change -> change.Source.OwnerId)
        let duplicateNames = changedSourceNames |> List.groupBy id |> List.tryFind (fun (_, values) -> values.Length > 1)
        match duplicateNames with
        | Some(name, _) -> fail "FLOW_BATCH_DUPLICATE_NAME" "A source-backed batch may change a word name only once." (Some name) None [] [ name ]
        | None -> ()
        let duplicateIds = changedSourceIds |> List.groupBy id |> List.tryFind (fun (_, values) -> values.Length > 1)
        match duplicateIds with
        | Some(identity, _) -> fail "FLOW_BATCH_ID_COLLISION" "A source-backed batch may change a stable owner ID only once." None None [] [ sprintf "%A" identity ]
        | None -> ()
        let validatedChanges: ValidatedFlowSourceChange list =
            sourceChanges
            |> List.map (fun (change: FlowSourceChange) ->
                let definition = validateFlowSourceDocument change.Source
                match change.RevisionIntent with
                | FlowWordRevisionIntent.Add(identity, revision) ->
                    if identity <> change.Source.OwnerId || revision <> change.Source.OwnerRevision then
                        fail "FLOW_SOURCE_CHANGE_REVISION_MISMATCH" "An added Flow source owner ID/revision must match its add intent." (Some definition.Name) (Some definition.Span)
                            [ sprintf "%A@%d" identity revision ] [ sprintf "%A@%d" change.Source.OwnerId change.Source.OwnerRevision ]
                | FlowWordRevisionIntent.Replace(identity, expectedRevision, revision) ->
                    if identity <> change.Source.OwnerId || revision <> change.Source.OwnerRevision then
                        fail "FLOW_SOURCE_CHANGE_REVISION_MISMATCH" "A replacement Flow source owner ID/revision must match its replacement intent." (Some definition.Name) (Some definition.Span)
                            [ sprintf "%A@%d" identity revision ] [ sprintf "%A@%d" change.Source.OwnerId change.Source.OwnerRevision ]
                    let previous =
                        context.CompilerContext.Words.TryFind definition.Name
                        |> Option.defaultWith (fun () ->
                            fail "FLOW_BATCH_REPLACE_MISSING" "Cannot replace a missing base word." (Some definition.Name) (Some definition.Span) [ "existing user word" ] [])
                    let previousIdentity =
                        context.CompilerContext.WordIds.TryFind definition.Name
                        |> Option.defaultWith (fun () ->
                            fail "FLOW_BATCH_BASE_ID_MAP" "The base word has no stable identity." (Some definition.Name) (Some definition.Span) [ "existing WordId" ] [])
                    if previousIdentity <> identity then
                        fail "FLOW_BATCH_REPLACE_ID_MISMATCH" "The replacement owner ID differs from the base word identity." (Some definition.Name) (Some definition.Span)
                            [ sprintf "%A" previousIdentity ] [ sprintf "%A" identity ]
                    if previous.Revision <> expectedRevision then
                        fail "FLOW_BATCH_STALE_REVISION" "Replacement intent does not match the base word revision." (Some definition.Name) (Some definition.Span)
                            [ string previous.Revision ] [ string expectedRevision ]
                    if revision <= expectedRevision then
                        fail "FLOW_BATCH_REVISION_NOT_ADVANCED" "Replacement revision must advance beyond the revision it replaces." (Some definition.Name) (Some definition.Span)
                            [ $"> {expectedRevision}" ] [ string revision ]
                    if previous.Builtin.IsSome || previous.Status = Primitive then
                        fail "FLOW_BATCH_REPLACE_PROTECTED" "Built-in or generated words cannot be replaced by Flow." (Some definition.Name) (Some definition.Span)
                            [ "user-authored word" ] [ definition.Name ]
                    if inventory.ExpectedFlowOwnerIds.Contains identity then
                        let baseline =
                            retainedBase
                            |> List.tryFind (fun retained -> retained.Document.OwnerName = definition.Name)
                            |> Option.defaultWith (fun () ->
                                fail "FLOW_SOURCE_REPLACE_OWNER_UNAUTHORED" "Replacing a Flow owner requires its exact current source document in the base inventory." (Some definition.Name) (Some definition.Span)
                                    [ "inventoried Flow owner source" ] [])
                        if baseline.Document.OwnerId <> identity then
                            fail "FLOW_BATCH_REPLACE_ID_MISMATCH" "The replacement owner ID differs from the inventoried base Flow owner ID." (Some definition.Name) (Some definition.Span)
                                [ sprintf "%A" baseline.Document.OwnerId ] [ sprintf "%A" identity ]
                        if baseline.Document.OwnerRevision <> expectedRevision then
                            fail "FLOW_BATCH_STALE_REVISION" "Replacement intent does not match the inventoried base Flow owner revision." (Some definition.Name) (Some definition.Span)
                                [ string baseline.Document.OwnerRevision ] [ string expectedRevision ]
                { Change =
                    { Definition = definition
                      RevisionIntent = change.RevisionIntent }
                  Source = change.Source })
        let changes = validatedChanges |> List.map (fun value -> value.Change)
        let sourceDocuments =
            validatedChanges
            |> List.map (fun value -> value.Source.OwnerName, value.Source)
            |> Map.ofList
        let replacedNames =
            validatedChanges
            |> List.choose (fun value ->
                match value.Change.RevisionIntent with
                | FlowWordRevisionIntent.Replace _ -> Some value.Source.OwnerName
                | FlowWordRevisionIntent.Add _ -> None)
            |> Set.ofList
        let retainedSources = retainedBase |> List.filter (fun retained -> not (replacedNames.Contains retained.Document.OwnerName))
        let artifacts = compileBatchWordsCore context changes sourceDocuments retainedSources
        let callBindings =
            artifacts.AuthoredOwners
            |> List.collect (fun owner ->
                let document =
                    owner.Document
                    |> Option.defaultWith (fun () -> fail "FLOW_BINDING_SOURCE_MISSING" "A source-backed batch owner has no exact authored source reference." (Some owner.OwnerName) None [] [])
                reconcileCallEvents artifacts.Compilation.Program owner.OwnerName owner.OwnerId owner.OwnerRevision owner.CallEvents
                |> List.map (fun site ->
                    { OwnerName = owner.OwnerName
                      OwnerId = owner.OwnerId
                      OwnerRevision = owner.OwnerRevision
                      Source = document.Reference
                      Site = site }))
        let flowOwnerIds = artifacts.AuthoredOwners |> List.map (fun owner -> owner.OwnerId) |> Set.ofList
        let finalSites =
            VerifiedIrProgram.inspect artifacts.Compilation.Program
            |> fun programData ->
                programData.SourceMap
                |> Map.filter (fun _ source -> source.SiteOwner |> Option.exists flowOwnerIds.Contains)
                |> sourceSites
        { LoweredWords = artifacts.AuthoredOwners |> List.map (fun owner -> owner.Lowered)
          Context = artifacts.Compilation.Context
          Program = artifacts.Compilation.Program
          SiteOrigins = finalSites
          CallBindings = callBindings }

    let compileBatchFlowSources
        (context: Context)
        (inventory: FlowSourceInventory)
        (sourceChanges: FlowSourceChange list)
        : FlowBoundBatchCompilation =
        compileBatchFlowSourcesCore false context inventory sourceChanges

    let private requireAttachmentVersion kind word span version =
        if version <> 1 then fail "FLOW_VERSION_UNSUPPORTED" $"Only Flow syntax version 1 is supported for Flow {kind} attachments." (Some word) (Some span) [ "1" ] [ string version ]

    let private lowerTestWithEvents
        (context: Context)
        (allocationOrigins: Map<SourceSpan, SourceSpan>)
        (flowTest: FlowTestDefinition)
        : FlowLoweredTest * FlowCallEvent list * FlowCallEvent list =
        FlowStructure.validateTestNesting flowTest
        requireAttachmentVersion "test" flowTest.Word flowTest.Span flowTest.SyntaxVersion
        let state = freshStateWith allocationOrigins (signatureCatalog context)
        rememberSpan state flowTest.Span
        rememberSpan state flowTest.HeaderSpan
        rememberSpan state flowTest.ExpectationSpan
        let bodyFragment, _ = lowerStatements context state Map.empty (FlowAstPath.FlowAstPath []) flowTest.Body
        let expected, expectationCallEvents =
            match flowTest.Expected with
            | FlowTestExpectation.Literal(literal, literalSpan) ->
                rememberSpan state literalSpan
                ExpectedValue literal, []
            | FlowTestExpectation.RuntimeError(code, codeSpan) ->
                rememberSpan state codeSpan
                ExpectedRuntimeError code, []
            | FlowTestExpectation.Expression expression ->
                inferExpression context state Map.empty expression |> ignore
                let loweredExpectation = lowerFlowExpression context state Map.empty (FlowAstPath.FlowAstPath []) expression
                ExpectedExpression loweredExpectation.Expressions, loweredExpectation.CallEvents
        let definition: TestDefinition =
            { Name = flowTest.CaseName
              Word = flowTest.Word
              Body = bodyFragment.Expressions
              Expected = expected
              SourceText = flowTest.SourceText
              Span = flowTest.Span }
        let projection = makeProjection state
        let origins = mergeOrigins context projection
        remapAttachmentDiagnostic origins (fun () -> Compiler.checkTest (knownTypes context) context.CompilerContext.Words definition |> ignore)
        { Definition = definition
          SourceText = flowTest.SourceText
          SyntaxVersion = flowTest.SyntaxVersion
          Projection = projection }, bodyFragment.CallEvents, expectationCallEvents

    let private lowerExampleWithEvents
        (context: Context)
        (allocationOrigins: Map<SourceSpan, SourceSpan>)
        (flowExample: FlowExampleDefinition)
        : FlowLoweredExample * FlowCallEvent list =
        FlowStructure.validateExampleNesting flowExample
        requireAttachmentVersion "example" flowExample.Word flowExample.Span flowExample.SyntaxVersion
        let state = freshStateWith allocationOrigins (signatureCatalog context)
        rememberSpan state flowExample.Span
        rememberSpan state flowExample.HeaderSpan
        rememberSpan state flowExample.ExpectationSpan
        rememberSpan state flowExample.ExpectedSpan
        let bodyFragment, _ = lowerStatements context state Map.empty (FlowAstPath.FlowAstPath []) flowExample.Body
        let definition: ExampleDefinition =
            { Name = flowExample.CaseName
              Word = flowExample.Word
              Body = bodyFragment.Expressions
              Expected = flowExample.Expected
              SourceText = flowExample.SourceText
              Span = flowExample.Span }
        let projection = makeProjection state
        let origins = mergeOrigins context projection
        remapAttachmentDiagnostic origins (fun () -> Compiler.checkExample (knownTypes context) context.CompilerContext.Words definition |> ignore)
        { Definition = definition
          SourceText = flowExample.SourceText
          SyntaxVersion = flowExample.SyntaxVersion
          Projection = projection }, bodyFragment.CallEvents

    let lowerTest (context: Context) (flowTest: FlowTestDefinition) : FlowLoweredTest =
        let lowered, _, _ = lowerTestWithEvents context context.SourceOrigins flowTest
        lowered

    let lowerExample (context: Context) (flowExample: FlowExampleDefinition) : FlowLoweredExample =
        let lowered, _ = lowerExampleWithEvents context context.SourceOrigins flowExample
        lowered

    let compileTest (context: Context) (verifiedProgram: VerifiedIrProgram) (flowTest: FlowTestDefinition) : CompiledTest =
        let lowered = lowerTest context flowTest
        let origins = mergeOrigins context lowered.Projection
        let body, expectationBody =
            Compiler.compileIrTestWithExpectationAgainstProgramWithSourceOrigins
                context.CompilerContext verifiedProgram lowered.Definition origins
        let bodySources = VerifiedIrBody.inspect body |> fun value -> value.BodySourceMap |> sourceSites
        let expectationSources =
            expectationBody
            |> Option.map (VerifiedIrBody.inspect >> fun value -> value.BodySourceMap |> sourceSites)
        { Lowered = lowered
          Program = verifiedProgram
          Body = body
          ExpectationBody = expectationBody
          BodySiteOrigins = bodySources
          ExpectationSiteOrigins = expectationSources }

    let compileExample (context: Context) (verifiedProgram: VerifiedIrProgram) (flowExample: FlowExampleDefinition) : CompiledExample =
        let lowered = lowerExample context flowExample
        let origins = mergeOrigins context lowered.Projection
        let body =
            Compiler.compileIrExampleAgainstProgramWithSourceOrigins
                context.CompilerContext verifiedProgram lowered.Definition origins
        let sites = VerifiedIrBody.inspect body |> fun value -> value.BodySourceMap |> sourceSites
        { Lowered = lowered
          Program = verifiedProgram
          Body = body
          SiteOrigins = sites }

    let private compileTestWithCallEvents
        (context: Context)
        (verifiedProgram: VerifiedIrProgram)
        (allocationOrigins: Map<SourceSpan, SourceSpan>)
        (flowTest: FlowTestDefinition)
        : CompiledTest * FlowCallEvent list * FlowCallEvent list =
        let lowered, actualEvents, expectedEvents = lowerTestWithEvents context allocationOrigins flowTest
        let origins = mergeOrigins context lowered.Projection
        let body, expectationBody =
            Compiler.compileIrTestWithExpectationAgainstProgramWithSourceOrigins
                context.CompilerContext verifiedProgram lowered.Definition origins
        let bodySources = VerifiedIrBody.inspect body |> fun value -> value.BodySourceMap |> sourceSites
        let expectationSources =
            expectationBody
            |> Option.map (VerifiedIrBody.inspect >> fun value -> value.BodySourceMap |> sourceSites)
        { Lowered = lowered
          Program = verifiedProgram
          Body = body
          ExpectationBody = expectationBody
          BodySiteOrigins = bodySources
          ExpectationSiteOrigins = expectationSources }, actualEvents, expectedEvents

    let private compileExampleWithCallEvents
        (context: Context)
        (verifiedProgram: VerifiedIrProgram)
        (allocationOrigins: Map<SourceSpan, SourceSpan>)
        (flowExample: FlowExampleDefinition)
        : CompiledExample * FlowCallEvent list =
        let lowered, events = lowerExampleWithEvents context allocationOrigins flowExample
        let origins = mergeOrigins context lowered.Projection
        let body =
            Compiler.compileIrExampleAgainstProgramWithSourceOrigins
                context.CompilerContext verifiedProgram lowered.Definition origins
        let sites = VerifiedIrBody.inspect body |> fun value -> value.BodySourceMap |> sourceSites
        { Lowered = lowered
          Program = verifiedProgram
          Body = body
          SiteOrigins = sites }, events

    /// Compile a host-built Flow test AST and reconcile its actual and expected
    /// expression call sites with detached verified IR. This does not bind the
    /// AST to source bytes; use the project-source API for source-backed rows.
    let compileTestWithCallBindings
        (context: Context)
        (verifiedProgram: VerifiedIrProgram)
        (flowTest: FlowTestDefinition)
        : CompiledCallBoundTest =
        let compiled, actualEvents, expectedEvents =
            compileTestWithCallEvents context verifiedProgram context.SourceOrigins flowTest
        let actualSites = reconcileDetachedCallEvents flowTest.Word compiled.Body actualEvents
        let expectedSites =
            compiled.ExpectationBody
            |> Option.map (fun body -> reconcileDetachedCallEvents flowTest.Word body expectedEvents)
        let callSites =
            [ FlowAttachmentBodyRole.Actual, actualSites ]
            @ (expectedSites |> Option.map (fun sites -> FlowAttachmentBodyRole.ExpectedExpression, sites) |> Option.toList)
            |> Map.ofList
        { Compiled = compiled
          CallSites = callSites }

    let private attachmentKeyOfDocument (document: FlowAttachmentSourceDocument) =
        { OwnerId = document.OwnerId
          Kind = document.Kind
          CaseName = document.CaseName }

    let private validateAttachmentKey (key: FlowAttachmentKey) =
        match key.OwnerId with
        | WordId raw when String.IsNullOrWhiteSpace raw ->
            fail "FLOW_ATTACHMENT_OWNER_ID_INVALID" "A Flow attachment key requires a nonempty stable owner ID." None None [ "nonempty WordId" ] [ raw ]
        | _ -> ()
        if String.IsNullOrWhiteSpace key.CaseName then
            fail "FLOW_ATTACHMENT_KEY_INVALID" "An attachment key requires a nonempty case name." None None [ "nonempty case name" ] [ key.CaseName ]

    let private validateFlowAttachmentSourceDocument (document: FlowAttachmentSourceDocument) : ParsedFlowAttachment =
        if String.IsNullOrWhiteSpace document.OwnerName then
            fail "FLOW_ATTACHMENT_OWNER_INVALID" "A Flow attachment source document requires an owner name." None None [ "nonempty word name" ] [ document.OwnerName ]
        if String.IsNullOrWhiteSpace document.SourceFile then
            fail "FLOW_ATTACHMENT_FILE_INVALID" "A Flow attachment source document requires a diagnostic source-file label." (Some document.OwnerName) None [ "nonempty source file" ] [ document.SourceFile ]
        if document.OwnerRevision < 0 then
            fail "FLOW_ATTACHMENT_REVISION_INVALID" "A Flow attachment owner revision must be nonnegative." (Some document.OwnerName) None [ "nonnegative revision" ] [ string document.OwnerRevision ]
        match document.OwnerId with
        | WordId raw when String.IsNullOrWhiteSpace raw ->
            fail "FLOW_ATTACHMENT_OWNER_ID_INVALID" "A Flow attachment requires a nonempty stable owner ID." (Some document.OwnerName) None [ "nonempty WordId" ] [ raw ]
        | _ -> ()
        let key = attachmentKeyOfDocument document
        validateAttachmentKey key
        let expectedObjectKind, kindName =
            match document.Kind with
            | FlowAttachmentKind.Test -> StorageObjectKind.TestDefinition, "test"
            | FlowAttachmentKind.Example -> StorageObjectKind.ExampleDefinition, "example"
        if document.Reference.Kind <> expectedObjectKind then
            fail "FLOW_ATTACHMENT_KIND_MISMATCH" $"A Flow {kindName} source reference must identify a {kindName}-definition object." (Some document.OwnerName) None
                [ string expectedObjectKind ] [ string document.Reference.Kind ]
        if isNull document.Content then
            fail "FLOW_ATTACHMENT_CONTENT_INVALID" "Flow attachment source content cannot be null." (Some document.OwnerName) None [ "UTF-8 source text" ] [ "null" ]
        let actualReference =
            try Storage.sourceObject expectedObjectKind document.Content |> fun source -> source.Reference
            with
            | :? EncoderFallbackException ->
                fail "FLOW_ATTACHMENT_UTF8_INVALID" "Flow attachment source must encode as strict UTF-8 without replacement characters." (Some document.OwnerName) None [ "valid UTF-16 source text" ] [ "unpaired surrogate" ]
        if actualReference <> document.Reference then
            fail "FLOW_ATTACHMENT_HASH_MISMATCH" "The supplied Flow attachment reference does not match the exact strict UTF-8 source bytes." (Some document.OwnerName) None
                [ actualReference.Hash ] [ document.Reference.Hash ]
        let parsed =
            match document.Kind with
            | FlowAttachmentKind.Test ->
                match FlowParser.parseTest document.SourceFile document.Content with
                | Ok test -> ParsedFlowTest test
                | Error problem -> raise (LanguageException { problem with Word = Some document.OwnerName })
            | FlowAttachmentKind.Example ->
                match FlowParser.parseExample document.SourceFile document.Content with
                | Ok example -> ParsedFlowExample example
                | Error problem -> raise (LanguageException { problem with Word = Some document.OwnerName })
        let (parsedOwner, parsedCase, parsedSpan) =
            match parsed with
            | ParsedFlowTest test -> test.Word, test.CaseName, test.Span
            | ParsedFlowExample example -> example.Word, example.CaseName, example.Span
        if parsedOwner <> document.OwnerName then
            fail "FLOW_ATTACHMENT_OWNER_MISMATCH" "The parsed attachment owner differs from its declared word name." (Some document.OwnerName) (Some parsedSpan)
                [ document.OwnerName ] [ parsedOwner ]
        if parsedCase <> document.CaseName then
            fail "FLOW_ATTACHMENT_CASE_MISMATCH" "The parsed attachment case differs from its declared attachment key." (Some document.OwnerName) (Some parsedSpan)
                [ document.CaseName ] [ parsedCase ]
        parsed

    let private ensureAttachmentOwner
        (context: Context)
        (ownerName: string)
        (ownerId: WordId)
        (ownerRevision: int option)
        (span: SourceSpan option) =
        let entry =
            context.CompilerContext.Words.TryFind ownerName
            |> Option.defaultWith (fun () -> fail "FLOW_ATTACHMENT_OWNER_MISSING" "A Flow attachment refers to a missing dictionary word." (Some ownerName) span [ "existing user word" ] [])
        let actualId =
            context.CompilerContext.WordIds.TryFind ownerName
            |> Option.defaultWith (fun () -> fail "FLOW_ATTACHMENT_OWNER_ID_MISSING" "A Flow attachment owner has no stable dictionary ID." (Some ownerName) span [ "WordId" ] [])
        if actualId <> ownerId then
            fail "FLOW_ATTACHMENT_OWNER_ID_MISMATCH" "A Flow attachment owner ID differs from the dictionary ID-to-name catalog." (Some ownerName) span
                [ sprintf "%A" actualId ] [ sprintf "%A" ownerId ]
        if entry.Builtin.IsSome || entry.Status = Primitive then
            fail "FLOW_ATTACHMENT_OWNER_PROTECTED" "Flow source attachments require a user-authored dictionary word." (Some ownerName) span [ "user word" ] [ ownerName ]
        match ownerRevision with
        | Some expected when entry.Revision <> expected || entry.Definition.Revision <> expected ->
            fail "FLOW_ATTACHMENT_OWNER_REVISION_MISMATCH" "A Flow attachment owner revision differs from the requested dictionary snapshot." (Some ownerName) span
                [ string entry.Revision ] [ string expected ]
        | _ -> ()
        entry

    let private validateFlowAttachmentInventory
        (context: Context)
        (baseFlowOwnerIds: Set<WordId>)
        (inventory: FlowAttachmentInventory)
        : RetainedFlowAttachment list =
        let duplicateKeys =
            inventory.Sources
            |> List.map attachmentKeyOfDocument
            |> List.groupBy id
            |> List.tryFind (fun (_, values) -> values.Length > 1)
        match duplicateKeys with
        | Some(key, _) ->
            fail "FLOW_ATTACHMENT_INVENTORY_DUPLICATE" "A Flow attachment inventory may contain at most one document per owner/kind/case key." None None [] [ sprintf "%A" key ]
        | None -> ()
        for key in inventory.ExpectedSources |> Map.toSeq |> Seq.map fst do validateAttachmentKey key
        let actualKeys = inventory.Sources |> List.map attachmentKeyOfDocument |> Set.ofList
        let expectedKeys = inventory.ExpectedSources |> Map.toSeq |> Seq.map fst |> Set.ofSeq
        if actualKeys <> expectedKeys then
            let missing = Set.difference expectedKeys actualKeys |> Set.toList
            let undeclared = Set.difference actualKeys expectedKeys |> Set.toList
            fail "FLOW_ATTACHMENT_INVENTORY_INCOMPLETE" "The Flow attachment inventory must exactly cover the host-declared key/reference map." None None
                (expectedKeys |> Set.toList |> List.map (sprintf "%A"))
                ([ yield! missing |> List.map (sprintf "missing %A")
                   yield! undeclared |> List.map (sprintf "undeclared %A") ])
        let parsed =
            inventory.Sources
            |> List.map (fun document ->
                let key = attachmentKeyOfDocument document
                let parsed = withAttachmentDiagnosticContext document None (fun () -> validateFlowAttachmentSourceDocument document)
                let expectedReference = inventory.ExpectedSources[key]
                if expectedReference <> document.Reference then
                    fail "FLOW_ATTACHMENT_INVENTORY_REFERENCE_MISMATCH" "An attachment's content-addressed source reference differs from the host-declared inventory mapping." (Some document.OwnerName) None
                        [ sprintf "%A" expectedReference ] [ sprintf "%A" document.Reference ]
                let parsedSpan =
                    match parsed with
                    | ParsedFlowTest test -> test.Span
                    | ParsedFlowExample example -> example.Span
                if not (baseFlowOwnerIds.Contains document.OwnerId) then
                    fail "FLOW_ATTACHMENT_OWNER_FRONTEND" "A base Flow test/example inventory may contain only cases attached to owners proven Flow by the host's word-source inventory." (Some document.OwnerName) (Some parsedSpan)
                        [ "owner ID in ExpectedFlowOwnerIds" ] [ sprintf "%A" document.OwnerId ]
                ensureAttachmentOwner context document.OwnerName document.OwnerId (Some document.OwnerRevision) (Some parsedSpan) |> ignore
                document, parsed)
            |> List.sortBy (fun (document, _) -> attachmentKeyOfDocument document)
        // Detached baselines are independently checked against one immutable base program.
        let baseProgram = Compiler.compileIrProgramWithSourceOrigins context.CompilerContext context.SourceOrigins
        let mutable allocationOrigins = context.SourceOrigins
        parsed
        |> List.map (fun (document, parsedAttachment) ->
            let key = attachmentKeyOfDocument document
            let baseSites, projection =
                withAttachmentDiagnosticContext document (Some parsedAttachment) (fun () ->
                    match parsedAttachment with
                    | ParsedFlowTest test ->
                        let compiled, actualEvents, expectedEvents = compileTestWithCallEvents context baseProgram allocationOrigins test
                        let sites =
                            [ FlowAttachmentBodyRole.Actual, reconcileDetachedCallEvents document.OwnerName compiled.Body actualEvents ]
                            @ (compiled.ExpectationBody
                               |> Option.map (fun body -> FlowAttachmentBodyRole.ExpectedExpression, reconcileDetachedCallEvents document.OwnerName body expectedEvents)
                               |> Option.toList)
                        Map.ofList sites, compiled.Lowered.Projection
                    | ParsedFlowExample example ->
                        let compiled, events = compileExampleWithCallEvents context baseProgram allocationOrigins example
                        Map.ofList [ FlowAttachmentBodyRole.Actual, reconcileDetachedCallEvents document.OwnerName compiled.Body events ], compiled.Lowered.Projection)
            let overlap = projection.SyntheticOrigins |> Map.toSeq |> Seq.tryFind (fun (marker, _) -> allocationOrigins.ContainsKey marker)
            match overlap with
            | Some(marker, _) -> fail "FLOW_ATTACHMENT_SOURCE_ORIGIN_COLLISION" "Attachment source markers must remain disjoint across the host inventory." (Some document.OwnerName) None [] [ sprintf "%A" marker ]
            | None -> ()
            allocationOrigins <- Map.fold (fun found marker origin -> Map.add marker origin found) allocationOrigins projection.SyntheticOrigins
            { Document = document
              Parsed = parsedAttachment
              BaseCallSites = baseSites })

    let private checkRetainedAttachmentBindings
        (document: FlowAttachmentSourceDocument)
        (baseline: Map<FlowAttachmentBodyRole, FlowCallSite list>)
        (proposed: Map<FlowAttachmentBodyRole, FlowCallSite list>) =
        let ownerName = document.OwnerName
        let key = attachmentKeyOfDocument document
        let baseRoles = baseline |> Map.toSeq |> Seq.map fst |> Set.ofSeq
        let proposedRoles = proposed |> Map.toSeq |> Seq.map fst |> Set.ofSeq
        if baseRoles <> proposedRoles then
            fail "FLOW_ATTACHMENT_CALL_REBOUND" $"Retained Flow attachment {key} changed its actual/expected body roles during re-resolution." (Some ownerName) None
                (baseRoles |> Set.toList |> List.map (sprintf "%A")) (proposedRoles |> Set.toList |> List.map (sprintf "%A"))
        for role in baseRoles do
            let baseByPath = baseline[role] |> List.map (fun site -> site.Path, site) |> Map.ofList
            let proposedByPath = proposed[role] |> List.map (fun site -> site.Path, site) |> Map.ofList
            let basePaths = baseByPath |> Map.toSeq |> Seq.map fst |> Set.ofSeq
            let proposedPaths = proposedByPath |> Map.toSeq |> Seq.map fst |> Set.ofSeq
            if basePaths <> proposedPaths then
                fail "FLOW_ATTACHMENT_CALL_REBOUND" $"Retained Flow attachment {key} changed its structural call-site set in body role {role}." (Some ownerName) None
                    (basePaths |> Set.toList |> List.map (sprintf "%A")) (proposedPaths |> Set.toList |> List.map (sprintf "%A"))
            for path in basePaths do
                if baseByPath[path].Target <> proposedByPath[path].Target then
                    fail "FLOW_ATTACHMENT_CALL_REBOUND" $"Retained Flow attachment {key} call in body role {role} at {path} would bind to a different stable target identity." (Some ownerName) (Some proposedByPath[path].Span)
                        [ sprintf "role=%A" role; sprintf "path=%A" path; sprintf "target=%A" baseByPath[path].Target ]
                        [ sprintf "role=%A" role; sprintf "path=%A" path; sprintf "target=%A" proposedByPath[path].Target ]

    let compileBatchFlowProjectSources
        (context: Context)
        (wordInventory: FlowSourceInventory)
        (wordChanges: FlowSourceChange list)
        (attachmentInventory: FlowAttachmentInventory)
        (attachmentChanges: FlowAttachmentChange list)
        : FlowBoundProjectCompilation =
        validateContextCatalog context
        if List.isEmpty wordChanges && List.isEmpty attachmentChanges then
            fail "FLOW_BATCH_EMPTY" "A Flow project-source batch must contain at least one word or attachment change." None None
                [ "one or more word or attachment changes" ] []

        let baseFlowOwnerIds = wordInventory.ExpectedFlowOwnerIds
        let retainedAttachments = validateFlowAttachmentInventory context baseFlowOwnerIds attachmentInventory
        let baseDocuments =
            retainedAttachments
            |> List.map (fun retained -> attachmentKeyOfDocument retained.Document, retained)
            |> Map.ofList
        let baseReferences = attachmentInventory.ExpectedSources
        let changedKeys = attachmentChanges |> List.map (function
            | FlowAttachmentChange.Add document -> attachmentKeyOfDocument document
            | FlowAttachmentChange.Replace(_, document) -> attachmentKeyOfDocument document
            | FlowAttachmentChange.Remove(key, _) -> key)
        match changedKeys |> List.groupBy id |> List.tryFind (fun (_, values) -> values.Length > 1) with
        | Some(key, _) ->
            fail "FLOW_ATTACHMENT_DUPLICATE_CHANGE" "A Flow project-source batch may contain at most one intent per owner/kind/case key." None None [] [ sprintf "%A" key ]
        | None -> ()

        let mutable finalDocuments = retainedAttachments |> List.map (fun retained -> attachmentKeyOfDocument retained.Document, retained.Document) |> Map.ofList
        let mutable finalParsed = retainedAttachments |> List.map (fun retained -> attachmentKeyOfDocument retained.Document, retained.Parsed) |> Map.ofList
        for change in attachmentChanges do
            match change with
            | FlowAttachmentChange.Add document ->
                let key = attachmentKeyOfDocument document
                validateAttachmentKey key
                if baseReferences.ContainsKey key then
                    fail "FLOW_ATTACHMENT_ADD_EXISTS" "An attachment add requires a key absent from the base source inventory." (Some document.OwnerName) None [] [ sprintf "%A" key ]
                let parsed = withAttachmentDiagnosticContext document None (fun () -> validateFlowAttachmentSourceDocument document)
                finalDocuments <- Map.add key document finalDocuments
                finalParsed <- Map.add key parsed finalParsed
            | FlowAttachmentChange.Replace(expectedPriorReference, document) ->
                let key = attachmentKeyOfDocument document
                validateAttachmentKey key
                let actualPriorReference =
                    baseReferences.TryFind key
                    |> Option.defaultWith (fun () -> fail "FLOW_ATTACHMENT_REPLACE_MISSING" "An attachment replacement requires an existing base source key." (Some document.OwnerName) None [ sprintf "%A" key ] [])
                if expectedPriorReference <> actualPriorReference then
                    fail "FLOW_ATTACHMENT_STALE_SOURCE" "The attachment replacement's expected prior source reference does not match the base inventory." (Some document.OwnerName) None
                        [ sprintf "%A" actualPriorReference ] [ sprintf "%A" expectedPriorReference ]
                let parsed = withAttachmentDiagnosticContext document None (fun () -> validateFlowAttachmentSourceDocument document)
                finalDocuments <- Map.add key document finalDocuments
                finalParsed <- Map.add key parsed finalParsed
            | FlowAttachmentChange.Remove(key, expectedPriorReference) ->
                validateAttachmentKey key
                let actualPriorReference =
                    baseReferences.TryFind key
                    |> Option.defaultWith (fun () -> fail "FLOW_ATTACHMENT_REMOVE_MISSING" "An attachment removal requires an existing base source key." None None [ sprintf "%A" key ] [])
                if expectedPriorReference <> actualPriorReference then
                    fail "FLOW_ATTACHMENT_STALE_SOURCE" "The attachment removal's expected prior source reference does not match the base inventory." None None
                        [ sprintf "%A" actualPriorReference ] [ sprintf "%A" expectedPriorReference ]
                finalDocuments <- Map.remove key finalDocuments
                finalParsed <- Map.remove key finalParsed

        // The word inventory is checked and lowered from the immutable base;
        // empty word changes are permitted when the batch only edits attachments.
        let wordCompilation = compileBatchFlowSourcesCore true context wordInventory wordChanges
        let changedKeySet = Set.ofList changedKeys
        let finalFlowOwnerIds =
            wordChanges
            |> List.fold (fun owners change ->
                let identity =
                    match change.RevisionIntent with
                    | FlowWordRevisionIntent.Add(wordId, _)
                    | FlowWordRevisionIntent.Replace(wordId, _, _) -> wordId
                Set.add identity owners) baseFlowOwnerIds
        let mutable activeDocuments = Map.empty
        for KeyValue(key, document) in finalDocuments do
            if not (finalFlowOwnerIds.Contains key.OwnerId) then
                fail "FLOW_ATTACHMENT_OWNER_FRONTEND" "A candidate Flow test/example must be attached to a base Flow owner or a word made Flow by this batch." (Some document.OwnerName) None
                    [ "owner ID in base Flow inventory or a Flow word Add/Replace intent" ] [ sprintf "%A" key.OwnerId ]
            let currentName =
                wordCompilation.Context.CompilerContext.WordIds
                |> Map.toSeq
                |> Seq.tryPick (fun (name, identity) -> if identity = key.OwnerId then Some name else None)
                |> Option.defaultWith (fun () -> fail "FLOW_ATTACHMENT_OWNER_MISSING" "A final Flow attachment owner ID is absent from the candidate word dictionary." (Some document.OwnerName) None [ sprintf "%A" key.OwnerId ] [])
            if currentName <> document.OwnerName then
                fail "FLOW_ATTACHMENT_OWNER_MISMATCH" "The final owner name for an attachment key differs from the document owner name." (Some document.OwnerName) None
                    [ currentName ] [ document.OwnerName ]
            let ownerEntry = ensureAttachmentOwner wordCompilation.Context document.OwnerName key.OwnerId None None
            let outputDocument =
                if changedKeySet.Contains key then
                    if document.OwnerRevision <> ownerEntry.Revision then
                        fail "FLOW_ATTACHMENT_OWNER_REVISION_MISMATCH" "An added or replaced attachment must declare the exact final owner revision." (Some document.OwnerName) None
                            [ string ownerEntry.Revision ] [ string document.OwnerRevision ]
                    document
                else
                    { document with OwnerRevision = ownerEntry.Revision }
            activeDocuments <- Map.add key outputDocument activeDocuments

        let mutable allocationOrigins = wordCompilation.Context.SourceOrigins
        let compiledAttachments = ResizeArray<FlowCompiledAttachment>()
        let attachmentBindings = ResizeArray<FlowAttachmentCallBinding>()
        for KeyValue(key, document) in activeDocuments do
            let parsed = finalParsed[key]
            let siteMap, projection, compiledAttachment =
                withAttachmentDiagnosticContext document (Some parsed) (fun () ->
                    let result =
                        match parsed with
                        | ParsedFlowTest flowTest ->
                            let compiled, actualEvents, expectedEvents =
                                compileTestWithCallEvents wordCompilation.Context wordCompilation.Program allocationOrigins flowTest
                            let actualSites = reconcileDetachedCallEvents document.OwnerName compiled.Body actualEvents
                            let expectedSites =
                                compiled.ExpectationBody
                                |> Option.map (fun body -> reconcileDetachedCallEvents document.OwnerName body expectedEvents)
                            let roles =
                                [ FlowAttachmentBodyRole.Actual, actualSites ]
                                @ (expectedSites |> Option.map (fun sites -> FlowAttachmentBodyRole.ExpectedExpression, sites) |> Option.toList)
                            Map.ofList roles, compiled.Lowered.Projection, FlowCompiledAttachment.Test(document, compiled)
                        | ParsedFlowExample flowExample ->
                            let compiled, events =
                                compileExampleWithCallEvents wordCompilation.Context wordCompilation.Program allocationOrigins flowExample
                            let sites = reconcileDetachedCallEvents document.OwnerName compiled.Body events
                            Map.ofList [ FlowAttachmentBodyRole.Actual, sites ], compiled.Lowered.Projection, FlowCompiledAttachment.Example(document, compiled)
                    let siteMap, _, _ = result
                    match baseDocuments.TryFind key with
                    | Some retained when not (changedKeySet.Contains key) ->
                        checkRetainedAttachmentBindings document retained.BaseCallSites siteMap
                    | _ -> ()
                    result)
            let overlap = projection.SyntheticOrigins |> Map.toSeq |> Seq.tryFind (fun (marker, _) -> allocationOrigins.ContainsKey marker)
            match overlap with
            | Some(marker, _) ->
                fail "FLOW_ATTACHMENT_SOURCE_ORIGIN_COLLISION" "Final attachment source markers must remain disjoint across the candidate project." (Some document.OwnerName) None [] [ sprintf "%A" marker ]
            | None -> ()
            allocationOrigins <- Map.fold (fun found marker origin -> Map.add marker origin found) allocationOrigins projection.SyntheticOrigins
            compiledAttachments.Add compiledAttachment
            for KeyValue(bodyRole, sites) in siteMap do
                for site in sites do
                    attachmentBindings.Add
                        { Attachment = key
                          OwnerName = document.OwnerName
                          OwnerRevision = document.OwnerRevision
                          Source = document.Reference
                          BodyRole = bodyRole
                          Site = site }
        { WordCompilation = wordCompilation
          Attachments = List.ofSeq compiledAttachments
          AttachmentBindings = List.ofSeq attachmentBindings }

    let parameterCatalog (definitions: FlowWordDefinition list) =
        definitions
        |> List.map (fun definition -> definition.Name, (definition.Parameters |> List.map (fun parameter -> parameter.Name)))
        |> Map.ofList
