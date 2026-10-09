namespace AgentLang

/// Explicit conversion from compiler-proven authored call sites to durable
/// metadata. Storage validates shape/membership; Runtime must still compare
/// complete site keys and target identities against the verified snapshot.
module FlowPersistence =
    let private target = function
        | FlowLowering.FlowCallTargetIdentity.UserWord(WordId identity) ->
            StoredCallTarget.UserWord identity
        | FlowLowering.FlowCallTargetIdentity.Primitive(PrimitiveId identity) ->
            StoredCallTarget.Primitive identity
        | FlowLowering.FlowCallTargetIdentity.GeneratedWord(WordId identity) ->
            StoredCallTarget.GeneratedWord identity

    let private form = function
        | FlowLowering.FlowCallForm.Direct -> StoredCallForm.Direct
        | FlowLowering.FlowCallForm.AbsoluteRoot -> StoredCallForm.AbsoluteRoot
        | FlowLowering.FlowCallForm.DotStage stage -> StoredCallForm.DotStage stage
        | FlowLowering.FlowCallForm.PropertyAccess field -> StoredCallForm.PropertyAccess field
        | FlowLowering.FlowCallForm.StaticCallback(stage, qualification) ->
            StoredCallForm.StaticCallback(stage, qualification)
        | FlowLowering.FlowCallForm.TestOverrideTarget -> StoredCallForm.TestOverrideTarget

    let private binding source caseName role (site: FlowLowering.FlowCallSite) : StoredCallBinding =
        { Source = source
          CaseName = caseName
          BodyRole = role
          Path = site.Path
          Form = form site.Form
          RequestedName = site.RequestedName
          Target = target site.Target }

    /// Preserve compiler traversal order. Storage owns serialization ordering;
    /// callers compare structural keys, never list positions or IR ordinals.
    let wordBindings (bindings: FlowLowering.FlowCallBinding list) =
        bindings
        |> List.map (fun item -> binding item.Source None StoredCallBodyRole.Definition item.Site)

    /// Expected-expression sites remain distinct from actual-body sites even
    /// when their structural paths coincide. Target revision is intentionally
    /// absent from the durable identity and remains verified by semantic IR.
    let attachmentBindings (bindings: FlowLowering.FlowAttachmentCallBinding list) =
        bindings
        |> List.map (fun item ->
            let caseName, role =
                match item.BodyRole with
                | FlowLowering.FlowAttachmentBodyRole.Actual -> Some item.Attachment.CaseName, StoredCallBodyRole.Actual
                | FlowLowering.FlowAttachmentBodyRole.ExpectedExpression -> Some item.Attachment.CaseName, StoredCallBodyRole.ExpectedExpression
                | FlowLowering.FlowAttachmentBodyRole.TestOverride -> None, StoredCallBodyRole.TestOverride
            binding item.Source caseName role item.Site)

    /// Bind each declaration header and fixture-body call to the exact shared
    /// wrapper source. Override rows are owner-scoped, not case-scoped.
    let testOverrideBindings (source: SourceRef) (overrides: FlowLowering.FlowTestOverrideBindings list) : StoredCallBinding list =
        if source.Kind <> StorageObjectKind.TestDefinition then
            invalidArg (nameof source) "Test override bindings must reference a test-definition source."
        overrides
        |> List.collect (fun item ->
            let expectedRoot = FlowAstPath.FlowAstPath [ FlowAstPathSegment.TestOverrideDefinition item.OverrideIndex ]
            if item.Header.Path <> expectedRoot || item.Header.Form <> FlowLowering.FlowCallForm.TestOverrideTarget then
                invalidArg (nameof overrides) "A test override header must bind its declaration root and target form."
            let header = binding source None StoredCallBodyRole.TestOverride item.Header
            let body =
                item.BodySites
                |> List.map (fun site ->
                    let rootedAtExpectedDeclaration =
                        match site.Path with
                        | FlowAstPath.FlowAstPath (FlowAstPathSegment.TestOverrideDefinition index :: tail) ->
                            index = item.OverrideIndex
                            && not (List.isEmpty tail)
                            && not (tail |> List.exists (function | FlowAstPathSegment.TestOverrideDefinition _ -> true | _ -> false))
                        | _ -> false
                    if not rootedAtExpectedDeclaration || site.Form = FlowLowering.FlowCallForm.TestOverrideTarget then
                        invalidArg (nameof overrides) "A test override body call must be rooted below its declaration."
                    binding source None StoredCallBodyRole.TestOverride site)
            header :: body)
