namespace AgentLang

open System.Collections.Generic

/// The transformed authored object and the complete mapping of its prior
/// resolved call sites. Source references still name the old immutable source
/// object; the Runtime replaces them after it renders and hashes the result.
type FlowRewriteResult<'Definition> =
    { Definition: 'Definition
      Bindings: StoredCallBinding list
      Changed: bool }

/// Pure, source-AST rewrite helpers for stable-identity Flow maintenance.
/// Stored targets are accepted from the last verified Runtime snapshot. The
/// helpers never resolve names or create new target identities.
module FlowRewrite =
    type private SiteKey = StoredCallBodyRole * FlowAstPath

    type private SiteShape =
        { Role: StoredCallBodyRole
          Path: FlowAstPath
          Form: StoredCallForm
          RequestedName: string
          Span: SourceSpan }

    let private pathChild (FlowAstPath.FlowAstPath prefix) (segment: FlowAstPathSegment) =
        FlowAstPath.FlowAstPath(prefix @ [ segment ])

    let private argumentSegment (form: StoredCallForm) (index: int) =
        match form with
        | StoredCallForm.Direct -> FlowAstPathSegment.CallArgument index
        | StoredCallForm.AbsoluteRoot -> FlowAstPathSegment.RootCallArgument index
        | StoredCallForm.DotStage _ -> FlowAstPathSegment.DotArgument index
        | StoredCallForm.PropertyAccess _ ->
            invalidArg (nameof form) "Record property accesses do not have explicit call argument paths."
        | StoredCallForm.StaticCallback _ ->
            invalidArg (nameof form) "Static callbacks do not use ordinary expression argument paths."
        | StoredCallForm.TestOverrideTarget ->
            invalidArg (nameof form) "Test override header bindings do not have call argument paths."

    let private callSite role path form requestedName span =
        { Role = role
          Path = path
          Form = form
          RequestedName = requestedName
          Span = span }

    let private isStaticCallbackStage = function
        | "map" | "filter" | "each" | "fold" -> true
        | _ -> false

    let private collectSites (roots: (StoredCallBodyRole * FlowExpression list * FlowStatement list) list) =
        let sites = ResizeArray<SiteShape>()

        let add role path form requestedName span =
            sites.Add(callSite role path form requestedName span)

        let rec visitExpression role path expression =
            match expression with
            | FlowExpression.Literal _
            | FlowExpression.Local _ -> ()
            | FlowExpression.Call(name, arguments, span) ->
                add role path StoredCallForm.Direct name span
                visitArguments role path StoredCallForm.Direct arguments
            | FlowExpression.RootCall(target, arguments, span) ->
                add role path StoredCallForm.AbsoluteRoot target.Name span
                visitArguments role path StoredCallForm.AbsoluteRoot arguments
            | FlowExpression.DotCall(receiver, stage, arguments, span) ->
                visitExpression role (pathChild path FlowAstPathSegment.DotReceiver) receiver
                match stage, arguments with
                | "fold", [ FlowArgument.Positional seed; FlowArgument.WordReference reference ] ->
                    visitExpression role (pathChild path (FlowAstPathSegment.DotArgument 0)) seed
                    add role (pathChild path (FlowAstPathSegment.DotArgument 1))
                        (StoredCallForm.StaticCallback(stage, reference.Qualification)) reference.Name reference.Span
                | stage, [ FlowArgument.WordReference reference ] when isStaticCallbackStage stage ->
                    add role (pathChild path (FlowAstPathSegment.DotArgument 0))
                        (StoredCallForm.StaticCallback(stage, reference.Qualification)) reference.Name reference.Span
                | _ ->
                    if arguments |> List.exists (function FlowArgument.WordReference _ -> true | _ -> false) then
                        Diagnostics.raiseError "FLOW_REWRITE_CALLBACK_SHAPE"
                            "A Flow word reference must use the static callback shape for its list stage."
                            None (Some span) [ "map/filter/each with one word reference; fold with one seed and one final word reference" ] []
                    add role path (StoredCallForm.DotStage stage) stage span
                    visitArguments role path (StoredCallForm.DotStage stage) arguments
            | FlowExpression.Property(receiver, field, span) ->
                add role path (StoredCallForm.PropertyAccess field) field span
                visitExpression role (pathChild path FlowAstPathSegment.PropertyReceiver) receiver
            | FlowExpression.Equality(left, right, span) ->
                add role path StoredCallForm.Direct "equals" span
                visitExpression role (pathChild path FlowAstPathSegment.EqualityLeft) left
                visitExpression role (pathChild path FlowAstPathSegment.EqualityRight) right
            | FlowExpression.If(condition, thenStatements, elseStatements, _) ->
                visitExpression role (pathChild path FlowAstPathSegment.IfCondition) condition
                visitStatements role (pathChild path (FlowAstPathSegment.IfThenStatement 0)) thenStatements
                visitStatements role (pathChild path (FlowAstPathSegment.IfElseStatement 0)) elseStatements
            | FlowExpression.Container(_, _, payload, _) ->
                payload |> Option.iter (visitExpression role (pathChild path FlowAstPathSegment.ContainerPayload))
            | FlowExpression.MatchOption(scrutinee, someCase, noneCase, _) ->
                visitExpression role (pathChild path FlowAstPathSegment.OptionScrutinee) scrutinee
                visitStatements role (pathChild path (FlowAstPathSegment.OptionSomeStatement 0)) someCase.Statements
                visitStatements role (pathChild path (FlowAstPathSegment.OptionNoneStatement 0)) noneCase.Statements
            | FlowExpression.MatchResult(scrutinee, okCase, errorCase, _) ->
                visitExpression role (pathChild path FlowAstPathSegment.ResultScrutinee) scrutinee
                visitStatements role (pathChild path (FlowAstPathSegment.ResultOkStatement 0)) okCase.Statements
                visitStatements role (pathChild path (FlowAstPathSegment.ResultErrorStatement 0)) errorCase.Statements
            | FlowExpression.MatchEnum(scrutinee, cases, _) ->
                visitExpression role (pathChild path FlowAstPathSegment.EnumScrutinee) scrutinee
                cases |> List.iteri (fun caseIndex caseValue ->
                    visitStatementsWithPath role path (fun statementIndex -> FlowAstPathSegment.EnumCaseStatement(caseIndex, statementIndex)) caseValue.Statements)

        and visitArguments role path form arguments =
            arguments
            |> List.iteri (fun index argument ->
                let child = pathChild path (argumentSegment form index)
                match argument with
                | FlowArgument.Positional expression
                | FlowArgument.Named(_, expression, _) -> visitExpression role child expression
                | FlowArgument.WordReference reference ->
                    Diagnostics.raiseError "FLOW_REWRITE_CALLBACK_SHAPE"
                        "A Flow word reference must belong to a static list callback stage."
                        None (Some reference.Span) [ "static callback reference" ] [ reference.Name ])

        and visitStatements role path statements =
            statements
            |> List.iteri (fun index statement ->
                let statementPath = pathChild path (FlowAstPathSegment.BlockStatement index)
                match statement with
                | FlowStatement.Let(_, value, _) ->
                    visitExpression role (pathChild statementPath FlowAstPathSegment.LetInitializer) value
                | FlowStatement.LetMany(_, value, _) ->
                    visitExpression role (pathChild statementPath FlowAstPathSegment.DestructureInitializer) value
                | FlowStatement.Evaluate value ->
                    visitExpression role (pathChild statementPath FlowAstPathSegment.EvaluateExpression) value
                | FlowStatement.Return(values, _) ->
                    values
                    |> List.iteri (fun outputIndex value ->
                        visitExpression role (pathChild statementPath (FlowAstPathSegment.ReturnOutput outputIndex)) value))

        and visitStatementsWithPath role path statementPathSegment statements =
            statements
            |> List.iteri (fun index statement ->
                let statementPath = pathChild path (statementPathSegment index)
                match statement with
                | FlowStatement.Let(_, value, _) ->
                    visitExpression role (pathChild statementPath FlowAstPathSegment.LetInitializer) value
                | FlowStatement.LetMany(_, value, _) ->
                    visitExpression role (pathChild statementPath FlowAstPathSegment.DestructureInitializer) value
                | FlowStatement.Evaluate value ->
                    visitExpression role (pathChild statementPath FlowAstPathSegment.EvaluateExpression) value
                | FlowStatement.Return(values, _) ->
                    values
                    |> List.iteri (fun outputIndex value ->
                        visitExpression role (pathChild statementPath (FlowAstPathSegment.ReturnOutput outputIndex)) value))

        let rootPath = FlowAstPath.FlowAstPath []
        for role, expressions, statements in roots do
            expressions |> List.iter (visitExpression role rootPath)
            visitStatements role rootPath statements
        List.ofSeq sites

    let private bindingKey (binding: StoredCallBinding) = binding.BodyRole, binding.Path

    let private validateBindingSet (owner: string) (expectedCase: string option) (allowedRoles: Set<StoredCallBodyRole>) (sites: SiteShape list) (bindings: StoredCallBinding list) =
        let siteMap = Dictionary<SiteKey, SiteShape>()
        for site in sites do
            let key = site.Role, site.Path
            if siteMap.ContainsKey key then
                Diagnostics.raiseError "FLOW_REWRITE_AST_SITE_DUPLICATE"
                    "Two Flow call-like sites occupy the same body role and structural path."
                    (Some owner) (Some site.Span) [] [ sprintf "%A" key ]
            siteMap.Add(key, site)

        let bindingMap = Dictionary<SiteKey, StoredCallBinding>()
        let mutable sourceReference: SourceRef option = None
        for binding in bindings do
            if binding.CaseName <> expectedCase then
                Diagnostics.raiseError "FLOW_REWRITE_BINDING_DOCUMENT_MISMATCH"
                    "A persisted call binding belongs to a different Flow document or case."
                    (Some owner) None [ expectedCase |> Option.defaultValue "<word>" ] [ binding.CaseName |> Option.defaultValue "<word>" ]
            if not (allowedRoles.Contains binding.BodyRole) then
                Diagnostics.raiseError "FLOW_REWRITE_BINDING_DOCUMENT_MISMATCH"
                    "A persisted call binding has a body role that does not belong to this Flow document."
                    (Some owner) None (allowedRoles |> Set.toList |> List.map string) [ string binding.BodyRole ]
            match sourceReference with
            | None -> sourceReference <- Some binding.Source
            | Some prior when prior <> binding.Source ->
                Diagnostics.raiseError "FLOW_REWRITE_BINDING_SOURCE_MISMATCH"
                    "All persisted call bindings supplied for one Flow document must reference the same source object."
                    (Some owner) None [ prior.Hash ] [ binding.Source.Hash ]
            | Some _ -> ()
            let key = bindingKey binding
            if bindingMap.ContainsKey key then
                Diagnostics.raiseError "FLOW_REWRITE_BINDING_DUPLICATE"
                    "A Flow document has duplicate persisted bindings for one body role and structural path."
                    (Some owner) None [] [ sprintf "%A" key ]
            match siteMap.TryGetValue key with
            | false, _ ->
                Diagnostics.raiseError "FLOW_REWRITE_BINDING_UNMAPPED"
                    "A persisted call binding does not map to a call-like site in the supplied Flow AST."
                    (Some owner) None [ "an AST call site at the stored path" ] [ sprintf "%A" key ]
            | true, site when binding.Form <> site.Form || binding.RequestedName <> site.RequestedName ->
                Diagnostics.raiseError "FLOW_REWRITE_BINDING_MISMATCH"
                    "A persisted call binding's form or requested name differs from its authored AST site."
                    (Some owner) (Some site.Span)
                    [ sprintf "%A %s" site.Form site.RequestedName ]
                    [ sprintf "%A %s" binding.Form binding.RequestedName ]
            | true, _ -> bindingMap.Add(key, binding)

        for KeyValue(key, site) in siteMap do
            if not (bindingMap.ContainsKey key) then
                Diagnostics.raiseError "FLOW_REWRITE_BINDING_MISSING"
                    "A call-like site in the supplied Flow AST has no persisted stable-target binding."
                    (Some owner) (Some site.Span) [ "one verified binding for this site" ] [ sprintf "%A" key ]
        bindingMap

    let private callFormAndName (newName: string) =
        if newName.Contains "." then StoredCallForm.Direct, newName
        else StoredCallForm.AbsoluteRoot, newName

    let private pathForCallArgument (form: StoredCallForm) (index: int) =
        let segment =
            match form with
            | StoredCallForm.Direct -> FlowAstPathSegment.CallArgument index
            | StoredCallForm.AbsoluteRoot -> FlowAstPathSegment.RootCallArgument index
            | StoredCallForm.TestOverrideTarget ->
                invalidArg (nameof form) "Test override header bindings do not have call argument paths."
            | _ -> invalidArg (nameof form) "A rewritten word call must be direct or absolute-root."
        segment

    let private namedCall (newName: string) (arguments: FlowArgument list) (span: SourceSpan) =
        if newName.Contains "." then
            FlowExpression.Call(newName, arguments, span)
        else
            let targetSpan =
                { span with
                    Length = max span.Length (newName.Length + 2) }
            FlowExpression.RootCall({ Name = newName; Span = targetSpan }, arguments, span)

    let private callbackQualification (newName: string) =
        if newName.Contains "." then FlowWordReferenceQualification.NamespaceQualified
        else FlowWordReferenceQualification.AbsoluteRoot

    let private withLanguageErrors action =
        try action ()
        with LanguageException diagnostic -> Error diagnostic

    let private rewriteDocument<'Definition>
        (newName: string)
        (target: StoredCallTarget)
        (owner: string)
        (expectedCase: string option)
        (allowedRoles: Set<StoredCallBodyRole>)
        (roots: (StoredCallBodyRole * FlowExpression list * FlowStatement list) list)
        (bindings: StoredCallBinding list)
        (rewriteAst: FlowAstPath
            -> (StoredCallBodyRole -> FlowAstPath -> FlowAstPath -> FlowStatement list -> FlowStatement list)
            -> (StoredCallBodyRole -> FlowAstPath -> FlowAstPath -> (int -> FlowAstPathSegment) -> FlowStatement list -> FlowStatement list)
            -> (StoredCallBodyRole -> FlowAstPath -> FlowAstPath -> FlowExpression -> FlowExpression)
            -> 'Definition)
        (rootsOfDefinition: 'Definition -> (StoredCallBodyRole * FlowExpression list * FlowStatement list) list)
        (render: 'Definition -> string)
        (parse: string -> string -> Result<'Definition, Diagnostic>)
        (sourceFile: string)
        (originalSource: string)
        : Result<FlowRewriteResult<'Definition>, Diagnostic> =
        withLanguageErrors (fun () ->
            let sites = collectSites roots
            let bindingMap = validateBindingSet owner expectedCase allowedRoles sites bindings
            let mapped = Dictionary<SiteKey, StoredCallBinding>()

            let recordSite role oldPath newPath oldForm oldNameForSite newForm newNameForSite span =
                let key = role, oldPath
                let binding =
                    match bindingMap.TryGetValue key with
                    | true, value -> value
                    | false, _ ->
                        Diagnostics.raiseError "FLOW_REWRITE_BINDING_MISSING"
                            "The rewrite traversal encountered a site without its prevalidated binding."
                            (Some owner) (Some span) [ "verified site binding" ] [ sprintf "%A" key ]
                if binding.Form <> oldForm || binding.RequestedName <> oldNameForSite then
                    Diagnostics.raiseError "FLOW_REWRITE_BINDING_MISMATCH"
                        "The stored call binding changed while traversing the Flow AST."
                        (Some owner) (Some span) [ sprintf "%A %s" oldForm oldNameForSite ] [ sprintf "%A %s" binding.Form binding.RequestedName ]
                if mapped.ContainsKey key then
                    Diagnostics.raiseError "FLOW_REWRITE_BINDING_DUPLICATE"
                        "A persisted binding was consumed more than once during Flow rewriting."
                        (Some owner) (Some span) [] [ sprintf "%A" key ]
                mapped.Add(key, { binding with Path = newPath; Form = newForm; RequestedName = newNameForSite })
                binding.Target = target

            let rec rewriteExpression role oldPath newPath expression =
                match expression with
                | FlowExpression.Literal _
                | FlowExpression.Local _ -> expression
                | FlowExpression.Call(name, arguments, span) ->
                    let binding = bindingMap[(role, oldPath)]
                    let rewrittenName = if binding.Target = target then newName else name
                    let newForm, newRequestedName =
                        if binding.Target = target then callFormAndName newName
                        else StoredCallForm.Direct, name
                    let newExpressionPath = newPath
                    let newArguments =
                        arguments
                        |> List.mapi (fun index argument ->
                            rewriteArgument role
                                (pathChild oldPath (FlowAstPathSegment.CallArgument index))
                                (pathChild newExpressionPath (pathForCallArgument newForm index))
                                argument)
                    recordSite role oldPath newExpressionPath StoredCallForm.Direct name newForm newRequestedName span |> ignore
                    if binding.Target = target then namedCall rewrittenName newArguments span
                    else FlowExpression.Call(name, newArguments, span)
                | FlowExpression.RootCall(rootTarget, arguments, span) ->
                    let binding = bindingMap[(role, oldPath)]
                    let rewrittenName = if binding.Target = target then newName else rootTarget.Name
                    let newForm, newRequestedName =
                        if binding.Target = target then callFormAndName newName
                        else StoredCallForm.AbsoluteRoot, rootTarget.Name
                    let newArguments =
                        arguments
                        |> List.mapi (fun index argument ->
                            rewriteArgument role
                                (pathChild oldPath (FlowAstPathSegment.RootCallArgument index))
                                (pathChild newPath (pathForCallArgument newForm index))
                                argument)
                    recordSite role oldPath newPath StoredCallForm.AbsoluteRoot rootTarget.Name newForm newRequestedName span |> ignore
                    if binding.Target = target then namedCall rewrittenName newArguments span
                    else FlowExpression.RootCall(rootTarget, newArguments, span)
                | FlowExpression.DotCall(receiver, stage, arguments, span) ->
                    match stage, arguments with
                    | "fold", [ FlowArgument.Positional seed; FlowArgument.WordReference reference ] ->
                        let newReceiver =
                            rewriteExpression role
                                (pathChild oldPath FlowAstPathSegment.DotReceiver)
                                (pathChild newPath FlowAstPathSegment.DotReceiver)
                                receiver
                        let newSeed =
                            rewriteExpression role
                                (pathChild oldPath (FlowAstPathSegment.DotArgument 0))
                                (pathChild newPath (FlowAstPathSegment.DotArgument 0))
                                seed
                        let callbackPath = pathChild oldPath (FlowAstPathSegment.DotArgument 1)
                        let callbackBinding = bindingMap[(role, callbackPath)]
                        let qualification, requestedName =
                            if callbackBinding.Target = target then callbackQualification newName, newName
                            else reference.Qualification, reference.Name
                        let newReference =
                            if callbackBinding.Target = target then
                                { reference with Name = newName; Qualification = qualification }
                            else reference
                        let callbackForm = StoredCallForm.StaticCallback(stage, qualification)
                        recordSite role callbackPath (pathChild newPath (FlowAstPathSegment.DotArgument 1))
                            (StoredCallForm.StaticCallback(stage, reference.Qualification)) reference.Name
                            callbackForm requestedName reference.Span |> ignore
                        FlowExpression.DotCall(newReceiver, stage, [ FlowArgument.Positional newSeed; FlowArgument.WordReference newReference ], span)
                    | callbackStage, [ FlowArgument.WordReference reference ] when isStaticCallbackStage callbackStage ->
                        let newReceiver =
                            rewriteExpression role
                                (pathChild oldPath FlowAstPathSegment.DotReceiver)
                                (pathChild newPath FlowAstPathSegment.DotReceiver)
                                receiver
                        let callbackPath = pathChild oldPath (FlowAstPathSegment.DotArgument 0)
                        let callbackBinding = bindingMap[(role, callbackPath)]
                        let qualification, requestedName =
                            if callbackBinding.Target = target then callbackQualification newName, newName
                            else reference.Qualification, reference.Name
                        let newReference =
                            if callbackBinding.Target = target then
                                { reference with Name = newName; Qualification = qualification }
                            else reference
                        let callbackForm = StoredCallForm.StaticCallback(callbackStage, qualification)
                        recordSite role callbackPath (pathChild newPath (FlowAstPathSegment.DotArgument 0))
                            (StoredCallForm.StaticCallback(callbackStage, reference.Qualification)) reference.Name
                            callbackForm requestedName reference.Span |> ignore
                        FlowExpression.DotCall(newReceiver, stage, [ FlowArgument.WordReference newReference ], span)
                    | _ ->
                        if arguments |> List.exists (function FlowArgument.WordReference _ -> true | _ -> false) then
                            Diagnostics.raiseError "FLOW_REWRITE_CALLBACK_SHAPE"
                                "A Flow word reference must use the static callback shape for its list stage."
                                (Some owner) (Some span) [ "map/filter/each with one word reference; fold with one seed and one final word reference" ] []
                        let binding = bindingMap[(role, oldPath)]
                        let isTarget = binding.Target = target
                        let newForm, newRequestedName =
                            if isTarget then callFormAndName newName
                            else StoredCallForm.DotStage stage, stage
                        if isTarget then
                            let receiverPath = pathChild oldPath FlowAstPathSegment.DotReceiver
                            let newReceiverPath = pathChild newPath (pathForCallArgument newForm 0)
                            let newReceiver = rewriteExpression role receiverPath newReceiverPath receiver
                            let rewrittenArguments =
                                arguments
                                |> List.mapi (fun index argument ->
                                    rewriteArgument role
                                        (pathChild oldPath (FlowAstPathSegment.DotArgument index))
                                        (pathChild newPath (pathForCallArgument newForm (index + 1)))
                                        argument)
                            recordSite role oldPath newPath (StoredCallForm.DotStage stage) stage newForm newRequestedName span |> ignore
                            namedCall newName (FlowArgument.Positional newReceiver :: rewrittenArguments) span
                        else
                            let newReceiver =
                                rewriteExpression role
                                    (pathChild oldPath FlowAstPathSegment.DotReceiver)
                                    (pathChild newPath FlowAstPathSegment.DotReceiver)
                                    receiver
                            let rewrittenArguments =
                                arguments
                                |> List.mapi (fun index argument ->
                                    rewriteArgument role
                                        (pathChild oldPath (FlowAstPathSegment.DotArgument index))
                                        (pathChild newPath (FlowAstPathSegment.DotArgument index))
                                        argument)
                            recordSite role oldPath newPath (StoredCallForm.DotStage stage) stage newForm newRequestedName span |> ignore
                            FlowExpression.DotCall(newReceiver, stage, rewrittenArguments, span)
                | FlowExpression.Property(receiver, field, span) ->
                    let binding = bindingMap[(role, oldPath)]
                    if binding.Target = target then
                        Diagnostics.raiseError "FLOW_REWRITE_PROPERTY_TARGET"
                            "A generated record-field accessor cannot be renamed by rewriting a word reference; change the record declaration and dependent sources together."
                            (Some owner) (Some span) [ "unchanged declared field name" ] [ field ]
                    let newReceiver =
                        rewriteExpression role
                            (pathChild oldPath FlowAstPathSegment.PropertyReceiver)
                            (pathChild newPath FlowAstPathSegment.PropertyReceiver)
                            receiver
                    recordSite role oldPath newPath
                        (StoredCallForm.PropertyAccess field) field
                        (StoredCallForm.PropertyAccess field) field span |> ignore
                    FlowExpression.Property(newReceiver, field, span)
                | FlowExpression.Equality(left, right, span) ->
                    let binding = bindingMap[(role, oldPath)]
                    if binding.Target = target then
                        Diagnostics.raiseError "FLOW_REWRITE_OPERATOR_TARGET"
                            "Equality is intrinsic syntax and cannot be rewritten as a renamed dictionary call."
                            (Some owner) (Some span) [ "unchanged equality operator" ] [ newName ]
                    let newLeft =
                        rewriteExpression role
                            (pathChild oldPath FlowAstPathSegment.EqualityLeft)
                            (pathChild newPath FlowAstPathSegment.EqualityLeft) left
                    let newRight =
                        rewriteExpression role
                            (pathChild oldPath FlowAstPathSegment.EqualityRight)
                            (pathChild newPath FlowAstPathSegment.EqualityRight) right
                    recordSite role oldPath newPath StoredCallForm.Direct "equals" StoredCallForm.Direct "equals" span |> ignore
                    FlowExpression.Equality(newLeft, newRight, span)
                | FlowExpression.If(condition, thenStatements, elseStatements, span) ->
                    FlowExpression.If(
                        rewriteExpression role
                            (pathChild oldPath FlowAstPathSegment.IfCondition)
                            (pathChild newPath FlowAstPathSegment.IfCondition) condition,
                        rewriteStatements role
                            (pathChild oldPath (FlowAstPathSegment.IfThenStatement 0))
                            (pathChild newPath (FlowAstPathSegment.IfThenStatement 0)) thenStatements,
                        rewriteStatements role
                            (pathChild oldPath (FlowAstPathSegment.IfElseStatement 0))
                            (pathChild newPath (FlowAstPathSegment.IfElseStatement 0)) elseStatements,
                        span)
                | FlowExpression.Container(kind, typeArguments, payload, span) ->
                    FlowExpression.Container(kind, typeArguments,
                        payload |> Option.map (rewriteExpression role
                            (pathChild oldPath FlowAstPathSegment.ContainerPayload)
                            (pathChild newPath FlowAstPathSegment.ContainerPayload)), span)
                | FlowExpression.MatchOption(scrutinee, someCase, noneCase, span) ->
                    FlowExpression.MatchOption(
                        rewriteExpression role
                            (pathChild oldPath FlowAstPathSegment.OptionScrutinee)
                            (pathChild newPath FlowAstPathSegment.OptionScrutinee) scrutinee,
                        { someCase with
                            Statements = rewriteStatements role
                                (pathChild oldPath (FlowAstPathSegment.OptionSomeStatement 0))
                                (pathChild newPath (FlowAstPathSegment.OptionSomeStatement 0)) someCase.Statements },
                        { noneCase with
                            Statements = rewriteStatements role
                                (pathChild oldPath (FlowAstPathSegment.OptionNoneStatement 0))
                                (pathChild newPath (FlowAstPathSegment.OptionNoneStatement 0)) noneCase.Statements },
                        span)
                | FlowExpression.MatchResult(scrutinee, okCase, errorCase, span) ->
                    FlowExpression.MatchResult(
                        rewriteExpression role
                            (pathChild oldPath FlowAstPathSegment.ResultScrutinee)
                            (pathChild newPath FlowAstPathSegment.ResultScrutinee) scrutinee,
                        { okCase with
                            Statements = rewriteStatements role
                                (pathChild oldPath (FlowAstPathSegment.ResultOkStatement 0))
                                (pathChild newPath (FlowAstPathSegment.ResultOkStatement 0)) okCase.Statements },
                        { errorCase with
                            Statements = rewriteStatements role
                                (pathChild oldPath (FlowAstPathSegment.ResultErrorStatement 0))
                                (pathChild newPath (FlowAstPathSegment.ResultErrorStatement 0)) errorCase.Statements },
                        span)
                | FlowExpression.MatchEnum(scrutinee, cases, span) ->
                    FlowExpression.MatchEnum(
                        rewriteExpression role
                            (pathChild oldPath FlowAstPathSegment.EnumScrutinee)
                            (pathChild newPath FlowAstPathSegment.EnumScrutinee) scrutinee,
                        cases
                        |> List.mapi (fun caseIndex caseValue ->
                            { caseValue with
                                Statements = rewriteStatementsWithPath role oldPath newPath
                                    (fun statementIndex -> FlowAstPathSegment.EnumCaseStatement(caseIndex, statementIndex)) caseValue.Statements }),
                        span)

            and rewriteArgument role oldPath newPath argument =
                match argument with
                | FlowArgument.Positional expression ->
                    FlowArgument.Positional(rewriteExpression role oldPath newPath expression)
                | FlowArgument.Named(name, expression, nameSpan) ->
                    FlowArgument.Named(name, rewriteExpression role oldPath newPath expression, nameSpan)
                | FlowArgument.WordReference reference ->
                    Diagnostics.raiseError "FLOW_REWRITE_CALLBACK_SHAPE"
                        "A Flow word reference must use the static callback shape for its list stage."
                        (Some owner) (Some reference.Span) [ "static callback reference" ] [ reference.Name ]

            and rewriteStatements role oldPath newPath statements =
                rewriteStatementsWithPath role oldPath newPath FlowAstPathSegment.BlockStatement statements

            and rewriteStatementsWithPath role oldPath newPath statementPathSegment statements =
                statements
                |> List.mapi (fun index statement ->
                    let oldStatementPath = pathChild oldPath (statementPathSegment index)
                    let newStatementPath = pathChild newPath (statementPathSegment index)
                    match statement with
                    | FlowStatement.Let(name, value, span) ->
                        FlowStatement.Let(name,
                            rewriteExpression role
                                (pathChild oldStatementPath FlowAstPathSegment.LetInitializer)
                                (pathChild newStatementPath FlowAstPathSegment.LetInitializer) value,
                            span)
                    | FlowStatement.LetMany(names, value, span) ->
                        FlowStatement.LetMany(names,
                            rewriteExpression role
                                (pathChild oldStatementPath FlowAstPathSegment.DestructureInitializer)
                                (pathChild newStatementPath FlowAstPathSegment.DestructureInitializer) value,
                            span)
                    | FlowStatement.Evaluate value ->
                        FlowStatement.Evaluate(rewriteExpression role
                            (pathChild oldStatementPath FlowAstPathSegment.EvaluateExpression)
                            (pathChild newStatementPath FlowAstPathSegment.EvaluateExpression) value)
                    | FlowStatement.Return(values, span) ->
                        FlowStatement.Return(
                            values
                            |> List.mapi (fun outputIndex value ->
                                rewriteExpression role
                                    (pathChild oldStatementPath (FlowAstPathSegment.ReturnOutput outputIndex))
                                    (pathChild newStatementPath (FlowAstPathSegment.ReturnOutput outputIndex)) value),
                            span))

            let rootPath = FlowAstPath.FlowAstPath []
            let rewritten = rewriteAst rootPath rewriteStatements rewriteStatementsWithPath rewriteExpression
            if mapped.Count <> bindings.Length then
                Diagnostics.raiseError "FLOW_REWRITE_BINDING_UNMAPPED"
                    "Not every persisted call binding was consumed exactly once during Flow rewriting."
                    (Some owner) None [ string bindings.Length ] [ string mapped.Count ]
            let mappedBindings =
                bindings
                |> List.map (fun binding ->
                    match mapped.TryGetValue(bindingKey binding) with
                    | true, value -> value
                    | false, _ ->
                        Diagnostics.raiseError "FLOW_REWRITE_BINDING_UNMAPPED"
                            "A persisted call binding did not receive a rewritten source site."
                            (Some owner) None [ "mapped site" ] [ sprintf "%A" (bindingKey binding) ])
            let source = render rewritten
            match parse sourceFile source with
            | Error diagnostic -> Error diagnostic
            | Ok parsed ->
                // The final parse is independently checked so the returned complete binding
                // set is guaranteed to address every call site in the canonical authored AST.
                let finalSites = parsed |> rootsOfDefinition |> collectSites
                let finalMap = validateBindingSet owner expectedCase allowedRoles finalSites mappedBindings
                if finalMap.Count <> mappedBindings.Length then
                    Diagnostics.raiseError "FLOW_REWRITE_FINAL_BINDING_MISMATCH"
                        "The canonical Flow parse did not preserve the complete mapped binding inventory."
                        (Some owner) None [ string mappedBindings.Length ] [ string finalMap.Count ]
                Ok { Definition = parsed
                     Bindings = mappedBindings
                     Changed = source <> originalSource })

    let rewriteWord (oldName: string) (newName: string) (target: StoredCallTarget) (definition: FlowWordDefinition) (bindings: StoredCallBinding list) : Result<FlowRewriteResult<FlowWordDefinition>, Diagnostic> =
        withLanguageErrors (fun () ->
            FlowStructure.validateWordNesting definition
            let roots = [ StoredCallBodyRole.Definition, [], definition.Body ]
            let rewrite rootPath rewriteStatements _ rewriteExpression =
                { definition with
                    Name = if definition.Name = oldName then newName else definition.Name
                    Body = rewriteStatements StoredCallBodyRole.Definition rootPath rootPath definition.Body }
            let rootsOf (parsed: FlowWordDefinition) : (StoredCallBodyRole * FlowExpression list * FlowStatement list) list =
                [ StoredCallBodyRole.Definition, [], parsed.Body ]
            rewriteDocument newName target definition.Name None (Set.singleton StoredCallBodyRole.Definition)
                roots bindings rewrite rootsOf FlowSource.renderWord
                (FlowParser.parseWordWithVersion definition.SyntaxVersion) definition.Span.File definition.SourceText)

    let rewriteTest (oldName: string) (newName: string) (target: StoredCallTarget) (definition: FlowTestDefinition) (bindings: StoredCallBinding list) : Result<FlowRewriteResult<FlowTestDefinition>, Diagnostic> =
        withLanguageErrors (fun () ->
            FlowStructure.validateTestNesting definition
            let roots =
                let expected =
                    match definition.Expected with
                    | FlowTestExpectation.Expression expression -> [ expression ]
                    | FlowTestExpectation.Literal _ | FlowTestExpectation.RuntimeError _ -> []
                [ StoredCallBodyRole.Actual, [], definition.Body
                  StoredCallBodyRole.ExpectedExpression, expected, [] ]
            let rewrite rootPath rewriteStatements _ rewriteExpression =
                { definition with
                    Word = if definition.Word = oldName then newName else definition.Word
                    Body = rewriteStatements StoredCallBodyRole.Actual rootPath rootPath definition.Body
                    Expected =
                        match definition.Expected with
                        | FlowTestExpectation.Expression expression ->
                            FlowTestExpectation.Expression(rewriteExpression StoredCallBodyRole.ExpectedExpression rootPath rootPath expression)
                        | other -> other }
            let rootsOf (parsed: FlowTestDefinition) : (StoredCallBodyRole * FlowExpression list * FlowStatement list) list =
                let expected =
                    match parsed.Expected with
                    | FlowTestExpectation.Expression expression -> [ expression ]
                    | FlowTestExpectation.Literal _ | FlowTestExpectation.RuntimeError _ -> []
                [ StoredCallBodyRole.Actual, [], parsed.Body
                  StoredCallBodyRole.ExpectedExpression, expected, [] ]
            rewriteDocument newName target definition.Word (Some definition.CaseName)
                (Set.ofList [ StoredCallBodyRole.Actual; StoredCallBodyRole.ExpectedExpression ])
                roots bindings rewrite rootsOf FlowSource.renderTest
                (FlowParser.parseTestWithVersion definition.SyntaxVersion) definition.Span.File definition.SourceText)

    /// Rewrite one shared test-file source object as a unit. Test attachments
    /// retain their case keys, while override body bindings are rooted beneath
    /// their declaration and the override header has its own identity binding.
    let rewriteTestFileSettings
        (oldName: string)
        (newName: string)
        (target: StoredCallTarget)
        (settings: FlowTestFileSettings)
        (bindings: StoredCallBinding list)
        : Result<FlowRewriteResult<FlowTestFileSettings>, Diagnostic> =
        withLanguageErrors (fun () ->
            let owner = settings.Tests |> List.tryHead |> Option.map (fun test -> test.Word) |> Option.defaultValue settings.ScopeName
            let caseNames = settings.Tests |> List.map (fun test -> test.CaseName) |> Set.ofList
            match bindings with
            | first :: rest when rest |> List.exists (fun binding -> binding.Source <> first.Source) ->
                Diagnostics.raiseError "FLOW_REWRITE_BINDING_SOURCE_MISMATCH"
                    "All persisted call bindings supplied for one test-file wrapper must reference the same source object."
                    (Some owner) None [] []
            | _ -> ()

            let pathSegments (FlowAstPath.FlowAstPath segments) = segments
            let key (binding: StoredCallBinding) = binding.CaseName, binding.BodyRole, binding.Path
            let mapped = Dictionary<string option * StoredCallBodyRole * FlowAstPath, StoredCallBinding>()
            let rewrittenTests = ResizeArray<FlowTestDefinition>()
            let addMapping (oldBinding: StoredCallBinding) (newBinding: StoredCallBinding) =
                let oldKey = key oldBinding
                if mapped.ContainsKey oldKey then
                    Diagnostics.raiseError "FLOW_REWRITE_BINDING_DUPLICATE"
                        "A test-file binding was mapped more than once during source rewriting."
                        (Some owner) None [] [ sprintf "%A" oldKey ]
                mapped.Add(oldKey, newBinding)

            let overrideRows = bindings |> List.filter (fun binding -> binding.BodyRole = StoredCallBodyRole.TestOverride)
            for binding in overrideRows do
                if binding.CaseName.IsSome then
                    Diagnostics.raiseError "FLOW_REWRITE_BINDING_DOCUMENT_MISMATCH"
                        "Override bindings are file-scoped and cannot name an individual test case."
                        (Some owner) None [ "<file-scope>" ] [ binding.CaseName.Value ]
                match pathSegments binding.Path with
                | FlowAstPathSegment.TestOverrideDefinition index :: _ when index < 0 || index >= settings.Overrides.Length ->
                    Diagnostics.raiseError "FLOW_REWRITE_BINDING_UNMAPPED"
                        "An override binding path names a declaration that is absent from the test-file wrapper."
                        (Some owner) None [ string settings.Overrides.Length ] [ string index ]
                | FlowAstPathSegment.TestOverrideDefinition _ :: _ -> ()
                | _ ->
                    Diagnostics.raiseError "FLOW_REWRITE_BINDING_UNMAPPED"
                        "An override binding path is not rooted at a test-file replacement declaration."
                        (Some owner) None [ "TestOverrideDefinition index" ] [ sprintf "%A" binding.Path ]

            for binding in bindings do
                match binding.BodyRole, binding.CaseName with
                | StoredCallBodyRole.TestOverride, None -> ()
                | (StoredCallBodyRole.Actual | StoredCallBodyRole.ExpectedExpression), Some caseName when caseNames.Contains caseName -> ()
                | _ ->
                    let expectedCaseBindings = (caseNames |> Set.toList |> List.map (fun name -> "case " + name)) @ [ "file-scoped override" ]
                    Diagnostics.raiseError "FLOW_REWRITE_BINDING_DOCUMENT_MISMATCH"
                        "A persisted binding does not belong to a nested test case or file-scoped replacement in this wrapper."
                        (Some owner) None expectedCaseBindings
                        [ sprintf "%A/%A" binding.BodyRole binding.CaseName ]

            for test in settings.Tests do
                let testBindings = bindings |> List.filter (fun binding -> binding.CaseName = Some test.CaseName)
                let rewritten =
                    match rewriteTest oldName newName target test testBindings with
                    | Ok value -> value
                    | Error diagnostic -> raise (LanguageException diagnostic)
                rewrittenTests.Add rewritten.Definition
                (List.zip testBindings rewritten.Bindings) |> List.iter (fun (before, after) -> addMapping before after)

            let rewrittenOverrides =
                settings.Overrides
                |> List.mapi (fun index overrideDefinition ->
                    let root = FlowAstPathSegment.TestOverrideDefinition index
                    let headerPath = FlowAstPath.FlowAstPath [ root ]
                    let headerRows =
                        overrideRows
                        |> List.filter (fun binding -> binding.Path = headerPath)
                    let header =
                        match headerRows with
                        | [ value ] -> value
                        | [] ->
                            Diagnostics.raiseError "FLOW_REWRITE_BINDING_MISSING"
                                "A test-file replacement header has no stable-target binding."
                                (Some overrideDefinition.Definition.Name) (Some overrideDefinition.TargetSpan)
                                [ "one TestOverrideTarget header binding" ] []
                        | values ->
                            Diagnostics.raiseError "FLOW_REWRITE_BINDING_DUPLICATE"
                                "A test-file replacement header has duplicate stable-target bindings."
                                (Some overrideDefinition.Definition.Name) (Some overrideDefinition.TargetSpan)
                                [ "one TestOverrideTarget header binding" ] [ string values.Length ]
                    if header.Form <> StoredCallForm.TestOverrideTarget || header.RequestedName <> overrideDefinition.Definition.Name then
                        Diagnostics.raiseError "FLOW_REWRITE_BINDING_MISMATCH"
                            "A replacement header binding does not match the authored override target."
                            (Some overrideDefinition.Definition.Name) (Some overrideDefinition.TargetSpan)
                            [ sprintf "%A %s" StoredCallForm.TestOverrideTarget overrideDefinition.Definition.Name ]
                            [ sprintf "%A %s" header.Form header.RequestedName ]
                    let bodyRows =
                        overrideRows
                        |> List.filter (fun binding ->
                            match pathSegments binding.Path with
                            | FlowAstPathSegment.TestOverrideDefinition pathIndex :: (_ :: _) -> pathIndex = index
                            | _ -> false)
                    let bodyBindings =
                        bodyRows
                        |> List.map (fun binding ->
                            match pathSegments binding.Path with
                            | FlowAstPathSegment.TestOverrideDefinition _ :: rest ->
                                { binding with BodyRole = StoredCallBodyRole.Definition; Path = FlowAstPath.FlowAstPath rest }
                            | _ ->
                                Diagnostics.raiseError "FLOW_REWRITE_BINDING_UNMAPPED"
                                    "An override body binding lost its declaration root during source rewriting."
                                    (Some overrideDefinition.Definition.Name) None [ "TestOverrideDefinition index" ] [ sprintf "%A" binding.Path ])
                    let bodyRewrite =
                        match rewriteWord "" newName target overrideDefinition.Definition bodyBindings with
                        | Ok value -> value
                        | Error diagnostic -> raise (LanguageException diagnostic)
                    (List.zip bodyRows bodyRewrite.Bindings)
                    |> List.iter (fun (original, after) ->
                        let mappedBody =
                            { after with
                                BodyRole = StoredCallBodyRole.TestOverride
                                CaseName = None
                                Path = FlowAstPath.FlowAstPath(root :: pathSegments after.Path) }
                        addMapping original mappedBody)
                    let renameHeader = header.Target = target
                    let definition =
                        if renameHeader then { bodyRewrite.Definition with Name = newName }
                        else bodyRewrite.Definition
                    let updated = { overrideDefinition with Definition = definition }
                    let mappedHeader =
                        { header with
                            RequestedName = if renameHeader then newName else header.RequestedName }
                    addMapping header mappedHeader
                    updated)

            let updatedSettings =
                { settings with
                    Overrides = rewrittenOverrides
                    Tests = List.ofSeq rewrittenTests }
            if mapped.Count <> bindings.Length then
                Diagnostics.raiseError "FLOW_REWRITE_BINDING_UNMAPPED"
                    "Not every persisted test-file binding was consumed exactly once during wrapper rewriting."
                    (Some owner) None [ string bindings.Length ] [ string mapped.Count ]
            let mappedBindings =
                bindings
                |> List.map (fun binding ->
                    match mapped.TryGetValue(key binding) with
                    | true, value -> value
                    | false, _ ->
                        Diagnostics.raiseError "FLOW_REWRITE_BINDING_UNMAPPED"
                            "A persisted test-file binding did not receive a rewritten source site."
                            (Some owner) None [ "mapped binding" ] [ sprintf "%A" (key binding) ])
            let source = FlowSource.renderTestFileSettings updatedSettings
            let parsed =
                FlowParser.parseTestFileSettingsWithVersion settings.SyntaxVersion settings.Span.File source
                |> Result.defaultWith (fun diagnostic -> raise (LanguageException diagnostic))
            // Revalidate the complete mapped inventory against the reparsed shared
            // wrapper, including header pseudo-sites and every nested case body.
            for test in parsed.Tests do
                let rows = mappedBindings |> List.filter (fun binding -> binding.CaseName = Some test.CaseName)
                let expected =
                    let expressions =
                        match test.Expected with
                        | FlowTestExpectation.Expression expression -> [ expression ]
                        | FlowTestExpectation.Literal _ | FlowTestExpectation.RuntimeError _ -> []
                    [ StoredCallBodyRole.Actual, [], test.Body
                      StoredCallBodyRole.ExpectedExpression, expressions, [] ]
                validateBindingSet test.Word (Some test.CaseName)
                    (Set.ofList [ StoredCallBodyRole.Actual; StoredCallBodyRole.ExpectedExpression ])
                    (collectSites expected) rows |> ignore
            for index, overrideDefinition in parsed.Overrides |> List.indexed do
                let root = FlowAstPathSegment.TestOverrideDefinition index
                let headerPath = FlowAstPath.FlowAstPath [ root ]
                let headerRows = mappedBindings |> List.filter (fun binding -> binding.Path = headerPath)
                match headerRows with
                | [ header ] when header.BodyRole = StoredCallBodyRole.TestOverride
                                 && header.CaseName.IsNone
                                 && header.Form = StoredCallForm.TestOverrideTarget
                                 && header.RequestedName = overrideDefinition.Definition.Name -> ()
                | _ ->
                    Diagnostics.raiseError "FLOW_REWRITE_FINAL_BINDING_MISMATCH"
                        "The canonical test-file source did not preserve its replacement header binding."
                        (Some overrideDefinition.Definition.Name) (Some overrideDefinition.TargetSpan)
                        [ "one matching TestOverrideTarget header binding" ] [ string headerRows.Length ]
                let rows =
                    mappedBindings
                    |> List.choose (fun binding ->
                        match pathSegments binding.Path with
                        | FlowAstPathSegment.TestOverrideDefinition pathIndex :: rest when pathIndex = index && not rest.IsEmpty ->
                            Some { binding with BodyRole = StoredCallBodyRole.Definition; Path = FlowAstPath.FlowAstPath rest }
                        | _ -> None)
                validateBindingSet overrideDefinition.Definition.Name None (Set.singleton StoredCallBodyRole.Definition)
                    (collectSites [ StoredCallBodyRole.Definition, [], overrideDefinition.Definition.Body ]) rows |> ignore
            Ok
                { Definition = parsed
                  Bindings = mappedBindings
                  Changed = source <> settings.SourceText })

    /// Removing one case rewrites the complete wrapper so its shared fixture
    /// declarations remain attached to every surviving case.
    let removeTestFileCase (caseName: string) (settings: FlowTestFileSettings) : Result<FlowTestFileSettings option, Diagnostic> =
        withLanguageErrors (fun () ->
            let remaining = settings.Tests |> List.filter (fun test -> test.CaseName <> caseName)
            if remaining.Length = settings.Tests.Length then
                Ok(Some settings)
            elif List.isEmpty remaining then
                Ok None
            else
                let updated = { settings with Tests = remaining }
                let source = FlowSource.renderTestFileSettings updated
                FlowParser.parseTestFileSettingsWithVersion settings.SyntaxVersion settings.Span.File source
                |> Result.map Some)

    let rewriteExample (oldName: string) (newName: string) (target: StoredCallTarget) (definition: FlowExampleDefinition) (bindings: StoredCallBinding list) : Result<FlowRewriteResult<FlowExampleDefinition>, Diagnostic> =
        withLanguageErrors (fun () ->
            FlowStructure.validateExampleNesting definition
            let roots = [ StoredCallBodyRole.Actual, [], definition.Body ]
            let rewrite rootPath rewriteStatements _ _ =
                { definition with
                    Word = if definition.Word = oldName then newName else definition.Word
                    Body = rewriteStatements StoredCallBodyRole.Actual rootPath rootPath definition.Body }
            let rootsOf (parsed: FlowExampleDefinition) : (StoredCallBodyRole * FlowExpression list * FlowStatement list) list =
                [ StoredCallBodyRole.Actual, [], parsed.Body ]
            rewriteDocument newName target definition.Word (Some definition.CaseName)
                (Set.singleton StoredCallBodyRole.Actual)
                roots bindings rewrite rootsOf FlowSource.renderExample
                (FlowParser.parseExampleWithVersion definition.SyntaxVersion) definition.Span.File definition.SourceText)
