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
                statements
                |> List.mapi (fun index statement ->
                    let oldStatementPath = pathChild oldPath (FlowAstPathSegment.BlockStatement index)
                    let newStatementPath = pathChild newPath (FlowAstPathSegment.BlockStatement index)
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
            let rewritten = rewriteAst rootPath rewriteStatements rewriteExpression
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
            let rewrite rootPath rewriteStatements rewriteExpression =
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
            let rewrite rootPath rewriteStatements rewriteExpression =
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

    let rewriteExample (oldName: string) (newName: string) (target: StoredCallTarget) (definition: FlowExampleDefinition) (bindings: StoredCallBinding list) : Result<FlowRewriteResult<FlowExampleDefinition>, Diagnostic> =
        withLanguageErrors (fun () ->
            FlowStructure.validateExampleNesting definition
            let roots = [ StoredCallBodyRole.Actual, [], definition.Body ]
            let rewrite rootPath rewriteStatements _ =
                { definition with
                    Word = if definition.Word = oldName then newName else definition.Word
                    Body = rewriteStatements StoredCallBodyRole.Actual rootPath rootPath definition.Body }
            let rootsOf (parsed: FlowExampleDefinition) : (StoredCallBodyRole * FlowExpression list * FlowStatement list) list =
                [ StoredCallBodyRole.Actual, [], parsed.Body ]
            rewriteDocument newName target definition.Word (Some definition.CaseName)
                (Set.singleton StoredCallBodyRole.Actual)
                roots bindings rewrite rootsOf FlowSource.renderExample
                (FlowParser.parseExampleWithVersion definition.SyntaxVersion) definition.Span.File definition.SourceText)
