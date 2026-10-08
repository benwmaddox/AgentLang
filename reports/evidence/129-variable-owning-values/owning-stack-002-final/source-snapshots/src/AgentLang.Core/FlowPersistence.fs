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
            let role =
                match item.BodyRole with
                | FlowLowering.FlowAttachmentBodyRole.Actual -> StoredCallBodyRole.Actual
                | FlowLowering.FlowAttachmentBodyRole.ExpectedExpression -> StoredCallBodyRole.ExpectedExpression
            binding item.Source (Some item.Attachment.CaseName) role item.Site)
