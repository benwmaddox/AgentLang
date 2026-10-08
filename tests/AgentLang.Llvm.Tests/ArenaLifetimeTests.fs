module AgentLang.Llvm.ArenaLifetimeTests

open System
open AgentLang
open AgentLang.Llvm

let private span file column =
    { File = file
      Line = 1
      Column = column
      Length = 1 }

let private wordEntry name inputs outputs effects body =
    let sourceSpan = span (name + ".agent") 1
    let definition =
        { Name = name
          Inputs = inputs
          Outputs = outputs
          Effects = effects
          Maturity = LibraryWord
          Revision = 1
          Documentation = "Arena lifetime provenance fixture."
          Body = body
          SourceText = name
          Span = sourceSpan }
    { Definition = definition
      Builtin = None
      Status = Persistent
      Maturity = LibraryWord
      Revision = 1 }

let private generatedRecordEntries (records: RecordDefinition list) =
    records
    |> List.collect (fun record ->
        let prefix = string (Char.ToLowerInvariant record.Name[0]) + record.Name.Substring(1)
        let constructor = wordEntry (prefix + ".new") (record.Fields |> List.map (fun field -> field.Type)) [ TNamed record.Name ] Set.empty []
        let constructor = { constructor with Builtin = Some(RecordConstructor record.Name) }
        let accessors =
            record.Fields
            |> List.map (fun field ->
                let accessor = wordEntry (prefix + "." + field.Name) [ TNamed record.Name ] [ field.Type ] Set.empty []
                { accessor with Builtin = Some(RecordAccessor(record.Name, field.Name)) })
        constructor :: accessors)

let private contextWith (extraWords: WordEntry list) (records: RecordDefinition list) : Compiler.IrLoweringContext =
    let recordMap = records |> List.map (fun record -> record.Name, record) |> Map.ofList
    let words =
        extraWords @ generatedRecordEntries records
        |> List.fold (fun found entry -> Map.add entry.Definition.Name entry found) Compiler.primitives
    let wordIds =
        words
        |> Map.toList
        |> List.map (fun (name, entry) ->
            let prefix =
                match entry.Builtin with
                | Some(BuiltinOp _) -> "primitive-"
                | Some _ -> "generated-"
                | None -> "user-"
            name, WordId(prefix + name))
        |> Map.ofList
    { Words = words
      Records = recordMap
      Scalars = Map.empty
      Enums = Map.empty
      WordIds = wordIds }

let private compileBody context name inputs expressions =
    let program = Compiler.compileIrProgram context
    Compiler.compileIrBodyAgainstProgram context program name inputs expressions

let private allScopeSitesInBlock (root: IrBlock) =
    let rec collect (block: IrBlock) =
        block.Code
        |> List.collect (fun instruction ->
            let children =
                match instruction.Operation with
                | IrOperation.Scope inner -> collect inner
                | IrOperation.If(thenBlock, elseBlock) -> collect thenBlock @ collect elseBlock
                | IrOperation.MatchOption(_, someBlock, noneBlock) -> collect someBlock @ collect noneBlock
                | IrOperation.MatchResult(_, _, okBlock, errorBlock) -> collect okBlock @ collect errorBlock
                | IrOperation.MatchEnum(_, cases) -> cases |> List.collect (snd >> collect)
                | _ -> []
            match instruction.Operation with
            | IrOperation.Scope _ -> instruction.Site :: children
            | _ -> children)
    collect root

let private allScopeSites (body: IrExecutableBody) = allScopeSitesInBlock body.BodyBlock

let private allStoreSlots (root: IrBlock) =
    let rec collect (block: IrBlock) =
        block.Code
        |> List.collect (fun instruction ->
            let nested =
                match instruction.Operation with
                | IrOperation.Scope inner -> collect inner
                | IrOperation.If(thenBlock, elseBlock) -> collect thenBlock @ collect elseBlock
                | IrOperation.MatchOption(_, someBlock, noneBlock) -> collect someBlock @ collect noneBlock
                | IrOperation.MatchResult(_, _, okBlock, errorBlock) -> collect okBlock @ collect errorBlock
                | IrOperation.MatchEnum(_, cases) -> cases |> List.collect (snd >> collect)
                | _ -> []
            match instruction.Operation with
            | IrOperation.StoreLocal slot -> slot :: nested
            | _ -> nested)
    collect root

let private scopeSiteAtFile (body: IrExecutableBody) file =
    allScopeSites body
    |> List.tryFind (fun site -> body.BodySourceMap.TryFind site |> Option.exists (fun source -> source.SiteSpan.File = file))
    |> Option.defaultWith (fun () -> failwith $"Missing scope site for {file}.")

let private scopeDecision (body: VerifiedIrBody) (analysis: ArenaLifetimeBodyAnalysis) file =
    let bodyData = VerifiedIrBody.inspect body
    let site = scopeSiteAtFile bodyData file
    analysis.ScopeExits
    |> Map.tryFind site
    |> Option.defaultWith (fun () -> failwith $"Missing rewind decision for scope {site}.")

let run () =
    let mutable assertions = 0
    let check name condition =
        assertions <- assertions + 1
        if not condition then failwith $"{name}: assertion failed"

    let emptyContext = contextWith [] []

    let deadOnly =
        compileBody emptyContext "arena-dead-only" [] [
            Scope([ Push(LString "dead", span "dead-only.scope.agent" 1); Call("drop", span "dead-only.drop.agent" 1) ], span "dead-only.scope.agent" 2)
        ]
    let deadOnlyAnalysis = ArenaLifetime.analyzeBody deadOnly
    let deadDecision = scopeDecision deadOnly deadOnlyAnalysis "dead-only.scope.agent"
    check "dead-only scope rewinds" deadDecision.AllowRewind
    check "dead-only explanation is deterministic and useful" (deadDecision.Reason.Contains("No outgoing value", StringComparison.Ordinal))
    check "empty frame rewinds after dead-only scope" deadOnlyAnalysis.FunctionExit.AllowRewind

    let escaping =
        compileBody emptyContext "arena-escaping" [] [
            Scope([ Push(LString "kept", span "escaping.value.agent" 1) ], span "escaping.scope.agent" 1)
        ]
    let escapingAnalysis = ArenaLifetime.analyzeBody escaping
    check "escaping String prevents scope rewind" (not (scopeDecision escaping escapingAnalysis "escaping.scope.agent").AllowRewind)
    check "escaping String prevents frame rewind" (not escapingAnalysis.FunctionExit.AllowRewind)

    let escapingLocal =
        compileBody emptyContext "arena-escaping-local" [] [
            Scope(
                [ Push(LString "local", span "local.value.agent" 1)
                  Let("value", span "local.let.agent" 1)
                  Load("value", span "local.load.agent" 1) ],
                span "local.scope.agent" 1)
        ]
    check "StoreLocal and LoadLocal preserve an escaping allocation origin" (not (scopeDecision escapingLocal (ArenaLifetime.analyzeBody escapingLocal) "local.scope.agent").AllowRewind)

    let concatEscapes =
        compileBody emptyContext "arena-concat-escape" [] [
            Scope(
                [ Push(LString "left", span "concat.escape.left.agent" 1)
                  Push(LString "right", span "concat.escape.right.agent" 1)
                  Call("string.concat", span "concat.escape.call.agent" 1) ],
                span "concat.escape.scope.agent" 1)
        ]
    check "String concatenation result belongs to the active scope" (not (scopeDecision concatEscapes (ArenaLifetime.analyzeBody concatEscapes) "concat.escape.scope.agent").AllowRewind)

    let arithmeticEscapes =
        compileBody emptyContext "arena-arithmetic-escape" [] [
            Scope(
                [ Push(LInt 2L, span "arithmetic.left.agent" 1)
                  Push(LInt 3L, span "arithmetic.right.agent" 1)
                  Call("add", span "arithmetic.call.agent" 1) ],
                span "arithmetic.scope.agent" 1)
        ]
    check "arithmetic result belongs to the active scope" (not (scopeDecision arithmeticEscapes (ArenaLifetime.analyzeBody arithmeticEscapes) "arithmetic.scope.agent").AllowRewind)

    let innerRecord =
        { Name = "Inner"
          Fields = [ { Name = "name"; Type = TString } ]
          Validator = None
          SourceText = "record Inner"
          Span = span "inner-record.agent" 1 }
    let outerRecord =
        { Name = "Outer"
          Fields = [ { Name = "child"; Type = TNamed "Inner" } ]
          Validator = None
          SourceText = "record Outer"
          Span = span "outer-record.agent" 1 }
    let recordContext = contextWith [] [ innerRecord; outerRecord ]
    let projected =
        compileBody recordContext "arena-projected-field" [] [
            Scope(
                [ Push(LString "projected", span "projected.value.agent" 1)
                  Call("inner.new", span "projected.inner.agent" 1)
                  Call("outer.new", span "projected.outer.agent" 1)
                  Call("outer.child", span "projected.child.agent" 1)
                  Call("inner.name", span "projected.name.agent" 1) ],
                span "projected.scope.agent" 1)
        ]
    let projectedAnalysis = ArenaLifetime.analyzeBody projected
    check "nested projected field keeps its backing record owner" (not (scopeDecision projected projectedAnalysis "projected.scope.agent").AllowRewind)
    check "projected field keeps frame allocation alive" (not projectedAnalysis.FunctionExit.AllowRewind)

    let temporaryUnderResult =
        compileBody emptyContext "arena-dead-interior" [] [
            Scope(
                [ Push(LString "temporary", span "interior.temporary.agent" 1)
                  Push(LString "result", span "interior.result.agent" 1) ],
                span "interior.scope.agent" 1)
        ]
    let interiorDecision = scopeDecision temporaryUnderResult (ArenaLifetime.analyzeBody temporaryUnderResult) "interior.scope.agent"
    check "escaping result preserves the suffix beneath it" (not interiorDecision.AllowRewind)

    let conditional =
        compileBody emptyContext "arena-conditional-escape" [] [
            Push(LString "pre-mark", span "conditional.prefix.agent" 1)
            Push(LBool true, span "conditional.condition.agent" 1)
            Scope(
                [ If(
                    [ Call("drop", span "conditional.then-drop.agent" 1)
                      Push(LString "new", span "conditional.then-value.agent" 1) ],
                    [],
                    span "conditional.if.agent" 1) ],
                span "conditional.scope.agent" 1)
        ]
    let conditionalAnalysis = ArenaLifetime.analyzeBody conditional
    check "one escaping conditional arm prevents scope rewind" (not (scopeDecision conditional conditionalAnalysis "conditional.scope.agent").AllowRewind)

    let shadow =
        compileBody emptyContext "arena-shadow" [] [
            Push(LString "outer", span "shadow.outer-value.agent" 1)
            Let("saved", span "shadow.outer-let.agent" 1)
            Scope(
                [ Push(LString "inner", span "shadow.inner-value.agent" 1)
                  Let("saved", span "shadow.inner-let.agent" 1)
                  Load("saved", span "shadow.inner-load.agent" 1)
                  Call("drop", span "shadow.inner-drop.agent" 1) ],
                span "shadow.scope.agent" 1)
            Load("saved", span "shadow.outer-load.agent" 1)
    ]
    let shadowAnalysis = ArenaLifetime.analyzeBody shadow
    let shadowStores = allStoreSlots (VerifiedIrBody.inspect shadow).BodyBlock
    check "child shadow writes the same verified local slot" (shadowStores.Length = 2 && (shadowStores |> List.distinct).Length = 1)
    check "same-slot child shadow restores the pre-mark outer local" (scopeDecision shadow shadowAnalysis "shadow.scope.agent").AllowRewind

    let pureCallDropped =
        compileBody emptyContext "arena-pure-call-dropped" [] [
            Scope(
                [ Push(LString "left", span "call-drop.left.agent" 1)
                  Push(LString "right", span "call-drop.right.agent" 1)
                  Call("string.concat", span "call-drop.call.agent" 1)
                  Call("drop", span "call-drop.drop.agent" 1) ],
                span "call-drop.scope.agent" 1)
        ]
    let pureCallDroppedAnalysis = ArenaLifetime.analyzeBody pureCallDropped
    check "dropped pure call output permits rewind" (scopeDecision pureCallDropped pureCallDroppedAnalysis "call-drop.scope.agent").AllowRewind

    let identity = wordEntry "user-identity" [ TString ] [ TString ] Set.empty []
    let callContext = contextWith [ identity ] []
    let uncertainCall =
        compileBody callContext "arena-call-output-uncertain" [ TString ] [
            Scope([ Call("user-identity", span "call-output.call.agent" 1) ], span "call-output.scope.agent" 1)
        ]
    let uncertainCallAnalysis = ArenaLifetime.analyzeBody uncertainCall
    check "unsummarized call output is tainted by current scope" (not (scopeDecision uncertainCall uncertainCallAnalysis "call-output.scope.agent").AllowRewind)

    let uncertainCallDropped =
        compileBody callContext "arena-call-output-dropped" [ TString ] [
            Scope(
                [ Call("user-identity", span "call-dropped.call.agent" 1)
                  Call("drop", span "call-dropped.drop.agent" 1) ],
                span "call-dropped.scope.agent" 1)
        ]
    check "dropped effect-free unsummarized call result permits rewind" (scopeDecision uncertainCallDropped (ArenaLifetime.analyzeBody uncertainCallDropped) "call-dropped.scope.agent").AllowRewind

    let nestedConsumed =
        compileBody emptyContext "arena-nested-consumed" [] [
            Scope(
                [ Scope([ Push(LString "inner", span "nested.inner-value.agent" 1) ], span "nested.inner-scope.agent" 1)
                  Call("drop", span "nested.consume.agent" 1) ],
                span "nested.outer-scope.agent" 1)
        ]
    let nestedConsumedAnalysis = ArenaLifetime.analyzeBody nestedConsumed
    check "nested result blocks its own rewind" (not (scopeDecision nestedConsumed nestedConsumedAnalysis "nested.inner-scope.agent").AllowRewind)
    check "outer scope rewinds after consuming nested result" (scopeDecision nestedConsumed nestedConsumedAnalysis "nested.outer-scope.agent").AllowRewind

    let returnsInput = compileBody emptyContext "arena-return-input" [ TString ] []
    let returnsFresh = compileBody emptyContext "arena-return-fresh" [] [ Push(LString "fresh", span "return-fresh.agent" 1) ]
    check "function exit can rewind when returning only an input" (ArenaLifetime.analyzeBody returnsInput).FunctionExit.AllowRewind
    check "function exit retains a fresh returned value" (not (ArenaLifetime.analyzeBody returnsFresh).FunctionExit.AllowRewind)

    let unsupported =
        compileBody emptyContext "arena-unmodeled-list" [] [
            Scope(
                [ ConstructContainer(ListEmpty, [ TString ], span "unmodeled.empty.agent" 1)
                  Call("drop", span "unmodeled.drop.agent" 1) ],
                span "unmodeled.scope.agent" 1)
        ]
    let unsupportedAnalysis = ArenaLifetime.analyzeBody unsupported
    let unsupportedDecision = scopeDecision unsupported unsupportedAnalysis "unmodeled.scope.agent"
    check "unmodeled verified operation stays conservative after drop" (not unsupportedDecision.AllowRewind)
    check "unmodeled decision explains retained uncertainty" (unsupportedDecision.Reason.Contains("unmodeled", StringComparison.Ordinal))

    let effectful =
        compileBody emptyContext "arena-effectful-call" [] [
            Scope(
                [ Push(LString "path", span "effect.path.agent" 1)
                  Push(LString "content", span "effect.content.agent" 1)
                  Call("file.write", span "effect.call.agent" 1)
                  Call("drop", span "effect.drop.agent" 1) ],
                span "effect.scope.agent" 1)
        ]
    check "effectful call retains the boundary after its result is dropped" (not (scopeDecision effectful (ArenaLifetime.analyzeBody effectful) "effect.scope.agent").AllowRewind)

    let duplicateDead =
        compileBody emptyContext "arena-duplicate-dead" [] [
            Push(LString "source", span "dup.source.agent" 1)
            Scope(
                [ Call("dup", span "dup.call.agent" 1)
                  Call("swap", span "dup.swap.agent" 1)
                  Call("drop", span "dup.drop-original.agent" 1) ],
                span "dup.scope.agent" 1)
            Call("drop", span "dup.drop-copy.agent" 1)
        ]
    check "explicit duplicate is allocated in the current scope" (not (scopeDecision duplicateDead (ArenaLifetime.analyzeBody duplicateDead) "dup.scope.agent").AllowRewind)

    let scopedFunction = wordEntry "scope-function" [] [] Set.empty [ Scope([], span "function-scope.agent" 1) ]
    let freshFunction = wordEntry "fresh-function" [] [ TString ] Set.empty [ Scope([ Push(LString "fresh", span "function-fresh.agent" 1) ], span "function-fresh-scope.agent" 1) ]
    let functionContext = contextWith [ scopedFunction; freshFunction ] []
    let functionProgram = Compiler.compileIrProgram functionContext
    let functionAnalyses = ArenaLifetime.analyzeProgram functionProgram
    let scopeFunctionId = functionContext.WordIds["scope-function"]
    let freshFunctionId = functionContext.WordIds["fresh-function"]
    let scopeFunction = functionProgram |> VerifiedIrProgram.inspect |> fun program -> program.FunctionsById[scopeFunctionId]
    let freshFunctionData = functionProgram |> VerifiedIrProgram.inspect |> fun program -> program.FunctionsById[freshFunctionId]
    let scopeFunctionSite = allScopeSitesInBlock scopeFunction.FunctionBody |> List.exactlyOne
    let freshFunctionSite = allScopeSitesInBlock freshFunctionData.FunctionBody |> List.exactlyOne
    check "verified functions have separate scope-site keys" (scopeFunctionSite <> freshFunctionSite)
    check "program analysis allows a dead function scope" functionAnalyses[scopeFunctionId].ScopeExits[scopeFunctionSite].AllowRewind
    check "program analysis keeps a fresh function result" (not functionAnalyses[freshFunctionId].FunctionExit.AllowRewind)

    let deadDecision = scopeDecision deadOnly deadOnlyAnalysis "dead-only.scope.agent"
    let unsafeDuplicate = ArenaLifetime.mergeDuplicateScopeDecision (scopeDecision escaping escapingAnalysis "escaping.scope.agent") deadDecision
    let unsafeDuplicateReversed = ArenaLifetime.mergeDuplicateScopeDecision deadDecision (scopeDecision escaping escapingAnalysis "escaping.scope.agent")
    check "duplicate scope-site decisions fail closed when either occurrence escapes" (not unsafeDuplicate.AllowRewind)
    check "duplicate scope-site merge is order independent" (unsafeDuplicate = unsafeDuplicateReversed)

    let duplicateSiteSource =
        compileBody emptyContext "arena-duplicate-site" [] [
            Scope([ Push(LString "escape", span "duplicate.escape.agent" 1) ], span "duplicate.escape.scope.agent" 1)
            Call("drop", span "duplicate.consume.agent" 1)
            Scope(
                [ Push(LString "dead", span "duplicate.dead-value.agent" 1)
                  Call("drop", span "duplicate.dead-drop.agent" 1) ],
                span "duplicate.dead.scope.agent" 1)
        ]
    let duplicateBody = VerifiedIrBody.inspect duplicateSiteSource
    let escapeSite, deadSite = allScopeSites duplicateBody |> function | [ first; second ] -> first, second | actual -> failwith $"Expected two scope sites, got {actual.Length}."
    let mutable seenScopes = 0
    let duplicateCode =
        duplicateBody.BodyBlock.Code
        |> List.map (fun instruction ->
            match instruction.Operation with
            | IrOperation.Scope _ ->
                let duplicate = seenScopes = 1
                seenScopes <- seenScopes + 1
                if duplicate then { instruction with Site = escapeSite } else instruction
            | _ -> instruction)
    let duplicateBlock = { duplicateBody.BodyBlock with Code = duplicateCode }
    let duplicateSourceMap = Map.remove deadSite duplicateBody.BodySourceMap
    let duplicateProgram = VerifiedIrProgram.inspect (VerifiedIrBody.program duplicateSiteSource)
    let duplicateCoverage = IrVerifier.coverageObligationsWithTypes duplicateProgram.NominalTypesByKey duplicateSourceMap duplicateBlock
    let malformedDuplicateBody =
        { duplicateBody with
            BodyBlock = duplicateBlock
            BodySourceMap = duplicateSourceMap
            BodyCoverage = duplicateCoverage }
    let duplicateBodyRejectedOrRetained =
        try
            let verified = IrVerifier.verifyBody (VerifiedIrBody.program duplicateSiteSource) malformedDuplicateBody
            let analysis = ArenaLifetime.analyzeBody verified
            let decision = analysis.ScopeExits[escapeSite]
            check "verified duplicate scope-site lookup fails closed" (not decision.AllowRewind)
            true
        with :? LanguageException ->
            check "verified duplicate scope sites are rejected" true
            true
    check "duplicate scope-site safety regression executed" duplicateBodyRejectedOrRetained

    let result = ArenaLifetime.analyzeBody deadOnly
    let repeat = ArenaLifetime.analyzeBody deadOnly
    check "analysis decisions are deterministic" (result = repeat)
    assertions
