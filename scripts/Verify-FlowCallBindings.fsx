open System
open System.IO
open System.Security.Cryptography
open AgentLang

let mutable assertions = 0

let check name condition =
    assertions <- assertions + 1
    if not condition then failwith $"{name}: assertion failed"

let equal name expected actual =
    assertions <- assertions + 1
    if expected <> actual then
        failwith $"{name}: expected {expected}, got {actual}"

let coreAssembly = typeof<FlowLowering.Context>.Assembly
let corePath = coreAssembly.Location
let coreInfo = FileInfo(corePath)
let coreHash =
    File.ReadAllBytes(corePath)
    |> SHA256.HashData
    |> Convert.ToHexString
    |> fun value -> value.ToLowerInvariant()

printfn "Core checkpoint: %s" corePath
printfn "Core LastWriteTimeUtc: %O" coreInfo.LastWriteTimeUtc
printfn "Core SHA256: %s" coreHash

let sourceSpan =
    { File = "<host-call-binding-probe>"
      Line = 1
      Column = 1
      Length = 96 }

let primitives: Map<string, WordEntry> = Compiler.primitives
let primitiveIds =
    primitives
    |> Map.map (fun name entry ->
        let prefix =
            match entry.Builtin with
            | Some(BuiltinOp _) -> "primitive-"
            | Some _ -> "generated-"
            | None -> "user-"
        WordId(prefix + name))

let context: FlowLowering.Context =
    { CompilerContext =
        { Words = primitives
          Records = Map.empty
          Scalars = Map.empty
          Enums = Map.empty
          WordIds = primitiveIds }
      ParameterNames = Map.empty
      SourceOrigins = Map.empty }

let literal value = FlowExpression.Literal(LInt value, sourceSpan)
let positional expression = FlowArgument.Positional expression
let call name arguments = FlowExpression.Call(name, arguments, sourceSpan)
let binary name left right = call name [ positional left; positional right ]

let condition =
    binary "int.less-than" (binary "add" (literal 2L) (literal 3L)) (literal 10L)

let thenValue =
    binary "multiply" (binary "subtract" (literal 9L) (literal 4L)) (literal 3L)

let elseValue =
    binary "add" (binary "add" (literal 1L) (literal 2L)) (literal 4L)

let conditional =
    FlowExpression.If(
        condition,
        [ FlowStatement.Return([ thenValue ], sourceSpan) ],
        [ FlowStatement.Return([ elseValue ], sourceSpan) ],
        sourceSpan)

let wordName = "probe.call-bindings"
let wordId = WordId "probe-call-bindings"
let word: FlowWordDefinition =
    { Name = wordName
      Parameters = []
      Outputs = [ TInt ]
      Effects = Set.empty
      EffectsDeclared = true
      Documentation = "Host-constructed structural call-binding probe."
      Body = [ FlowStatement.Return([ conditional ], sourceSpan) ]
      SourceText = "(host-constructed AST; not parsed or persisted)"
      Span = sourceSpan
      SyntaxVersion = 1 }

let compiled = FlowLowering.compileWordWithCallBindings context wordId word
let callSites = compiled.CallSites
let requestedNames = callSites |> List.map (fun site -> site.RequestedName)
let expectedNames = [ "add"; "int.less-than"; "subtract"; "multiply"; "add"; "add" ]

equal "all nested condition and branch calls are captured in lowering order" expectedNames requestedNames
equal "every call has the deliberately identical source span" (List.replicate expectedNames.Length sourceSpan) (callSites |> List.map (fun site -> site.Span))
equal "all call sites have distinct structural AST paths" callSites.Length (callSites |> List.map (fun site -> site.Path) |> Set.ofList |> Set.count)
equal "every call resolves to its exact primitive target identity"
    (expectedNames |> List.map (fun name -> FlowLowering.FlowCallTargetIdentity.Primitive(PrimitiveId name)))
    (callSites |> List.map (fun site -> site.Target))
equal "all sites retain the direct call form" (List.replicate expectedNames.Length FlowLowering.FlowCallForm.Direct) (callSites |> List.map (fun site -> site.Form))
equal "primitive targets carry no mutable word revision" (List.replicate expectedNames.Length None) (callSites |> List.map (fun site -> site.TargetRevision))

let path segments = FlowAstPath.FlowAstPath segments
let expectedPaths =
    [ path [ FlowAstPathSegment.BlockStatement 0; FlowAstPathSegment.ReturnOutput 0; FlowAstPathSegment.IfCondition; FlowAstPathSegment.CallArgument 0 ]
      path [ FlowAstPathSegment.BlockStatement 0; FlowAstPathSegment.ReturnOutput 0; FlowAstPathSegment.IfCondition ]
      path [ FlowAstPathSegment.BlockStatement 0; FlowAstPathSegment.ReturnOutput 0; FlowAstPathSegment.IfThenStatement 0; FlowAstPathSegment.BlockStatement 0; FlowAstPathSegment.ReturnOutput 0; FlowAstPathSegment.CallArgument 0 ]
      path [ FlowAstPathSegment.BlockStatement 0; FlowAstPathSegment.ReturnOutput 0; FlowAstPathSegment.IfThenStatement 0; FlowAstPathSegment.BlockStatement 0; FlowAstPathSegment.ReturnOutput 0 ]
      path [ FlowAstPathSegment.BlockStatement 0; FlowAstPathSegment.ReturnOutput 0; FlowAstPathSegment.IfElseStatement 0; FlowAstPathSegment.BlockStatement 0; FlowAstPathSegment.ReturnOutput 0; FlowAstPathSegment.CallArgument 0 ]
      path [ FlowAstPathSegment.BlockStatement 0; FlowAstPathSegment.ReturnOutput 0; FlowAstPathSegment.IfElseStatement 0; FlowAstPathSegment.BlockStatement 0; FlowAstPathSegment.ReturnOutput 0 ] ]

equal "paths identify condition, then branch, and else branch calls" expectedPaths (callSites |> List.map (fun site -> site.Path))

let rec authoredResolvedCallsInBlock (block: IrBlock) =
    block.Code
    |> List.collect (fun instruction ->
        match instruction.Operation with
        | IrOperation.Call call
        | IrOperation.ListMap(call, _, _)
        | IrOperation.ListFilter(call, _)
        | IrOperation.ListEach(call, _) -> [ call ]
        | IrOperation.MakeRecord(call, _)
        | IrOperation.GetRecordField(call, _, _)
        | IrOperation.UnwrapScalar(call, _) -> [ call ]
        | IrOperation.WrapScalar(call, _, _) -> [ call ]
        | IrOperation.Scope nested -> authoredResolvedCallsInBlock nested
        | IrOperation.If(thenBlock, elseBlock) -> authoredResolvedCallsInBlock thenBlock @ authoredResolvedCallsInBlock elseBlock
        | IrOperation.MatchOption(_, someBlock, noneBlock) -> authoredResolvedCallsInBlock someBlock @ authoredResolvedCallsInBlock noneBlock
        | IrOperation.MatchResult(_, _, okBlock, errorBlock) -> authoredResolvedCallsInBlock okBlock @ authoredResolvedCallsInBlock errorBlock
        | _ -> [])

let programData = VerifiedIrProgram.inspect compiled.Program
let functionValue = programData.FunctionsById[wordId]
let irCalls = authoredResolvedCallsInBlock functionValue.FunctionBody

equal "verified IR contains the same ordered resolved names" expectedNames (irCalls |> List.map (fun call -> call.ResolvedName))
equal "verified IR calls retain the same primitive identities"
    (expectedNames |> List.map (fun name -> PrimitiveTarget(PrimitiveId name)))
    (irCalls |> List.map (fun call -> call.ResolvedTarget))

let host: IrInterpreterHost =
    { PreflightEffects = fun _ _ _ -> ()
      ChargeInstruction = fun _ _ -> ()
      RecordBranchOutcome = fun _ _ _ -> ()
      RecordUse = ignore
      InvokeEffect = fun effect -> failwithf "Unexpected effect in pure probe: %A" effect
      WordDefinitionSpan = fun _ -> None
      PrimitiveDefinitionSpan = fun _ -> None }

let invokeBody =
    Compiler.compileIrBodyAgainstProgramWithSourceOrigins
        compiled.Context.CompilerContext
        compiled.Program
        "invoke-call-binding-probe"
        []
        [ Call(wordName, sourceSpan) ]
        compiled.Context.SourceOrigins

equal "verified program executes the selected true branch" [ IntValue 15L ] (IrInterpreter.executeBody host "invoke-call-binding-probe" invokeBody)

let migrationOwnerName = "probe.stack-to-flow"
let migrationOwnerId = WordId "migration-owner-stable-id"
let migrationCallerName = "probe.stack-caller"
let migrationCallerId = WordId "migration-stack-caller-id"

let stackOwnerDefinition: WordDefinition =
    { Name = migrationOwnerName
      Inputs = [ TInt ]
      Outputs = [ TInt ]
      Effects = Set.empty
      Maturity = LibraryWord
      Revision = 1
      Documentation = "Existing stack-authored word being migrated."
      Body = [ Push(LInt 1L, sourceSpan); Call("add", sourceSpan) ]
      SourceText = "(legacy stack definition)"
      Span = sourceSpan }

let stackCallerDefinition: WordDefinition =
    { Name = migrationCallerName
      Inputs = []
      Outputs = [ TInt ]
      Effects = Set.empty
      Maturity = ProjectWord
      Revision = 1
      Documentation = "Retained stack-authored caller."
      Body = [ Push(LInt 40L, sourceSpan); Call(migrationOwnerName, sourceSpan) ]
      SourceText = "(legacy stack caller)"
      Span = sourceSpan }

let stackOwnerEntry: WordEntry =
    { Definition = stackOwnerDefinition
      Builtin = None
      Status = Persistent
      Maturity = LibraryWord
      Revision = 1 }

let stackCallerEntry: WordEntry =
    { Definition = stackCallerDefinition
      Builtin = None
      Status = Persistent
      Maturity = ProjectWord
      Revision = 1 }

let migrationWords =
    primitives
    |> Map.add migrationOwnerName stackOwnerEntry
    |> Map.add migrationCallerName stackCallerEntry

let migrationWordIds =
    primitiveIds
    |> Map.add migrationOwnerName migrationOwnerId
    |> Map.add migrationCallerName migrationCallerId

let migrationContext: FlowLowering.Context =
    { CompilerContext =
        { Words = migrationWords
          Records = Map.empty
          Scalars = Map.empty
          Enums = Map.empty
          WordIds = migrationWordIds }
      ParameterNames = Map.empty
      SourceOrigins = Map.empty }

let emptyBaseInventory: FlowLowering.FlowSourceInventory =
    { ExpectedFlowOwnerIds = Set.empty
      Sources = [] }

let baseMigrationProgram =
    Compiler.compileIrProgramWithSourceOrigins migrationContext.CompilerContext migrationContext.SourceOrigins

let executeMigrationCaller (ownerContext: FlowLowering.Context) (program: VerifiedIrProgram) executionName =
    let executable =
        Compiler.compileIrBodyAgainstProgramWithSourceOrigins
            ownerContext.CompilerContext
            program
            executionName
            []
            [ Call(migrationCallerName, sourceSpan) ]
            ownerContext.SourceOrigins
    IrInterpreter.executeBody host executionName executable

equal "stack-authored caller executes the base stack word" [ IntValue 41L ]
    (executeMigrationCaller migrationContext baseMigrationProgram "invoke-stack-before-migration")

let migrationSource =
    """word probe.stack-to-flow(value: Int) -> Int {
    effects none
    return add(value, 2)
}"""

let migrationSourceObject = Storage.sourceObject StorageObjectKind.WordDefinition migrationSource
let migrationSourceDocument: FlowLowering.FlowSourceDocument =
    { OwnerName = migrationOwnerName
      OwnerId = migrationOwnerId
      OwnerRevision = 2
      Reference = migrationSourceObject.Reference
      SourceFile = "probe-migration.flow"
      Content = migrationSource
      SyntaxVersion = 1
      EffectsDeclared = true }

let migrationChange: FlowLowering.FlowSourceChange =
    { RevisionIntent = FlowLowering.FlowWordRevisionIntent.Replace(migrationOwnerId, 1, 2)
      Source = migrationSourceDocument }

let migrated = FlowLowering.compileBatchFlowSources migrationContext emptyBaseInventory [ migrationChange ]
let migratedOwner = migrated.Context.CompilerContext.Words[migrationOwnerName]

equal "source-backed stack-to-Flow replacement preserves stable owner ID" migrationOwnerId migrated.Context.CompilerContext.WordIds[migrationOwnerName]
equal "replacement preserves the old entry status" Persistent migratedOwner.Status
equal "replacement preserves the old entry maturity" LibraryWord migratedOwner.Maturity
equal "replacement advances entry revision" 2 migratedOwner.Revision
equal "replacement advances definition revision" 2 migratedOwner.Definition.Revision
equal "replacement preserves definition maturity" LibraryWord migratedOwner.Definition.Maturity

let ownerBindings = migrated.CallBindings |> List.filter (fun binding -> binding.OwnerName = migrationOwnerName)
equal "source-backed replacement emits one owner binding" 1 ownerBindings.Length
let migrationBinding = ownerBindings.Head
equal "binding retains the exact new WordDefinition source reference" migrationSourceObject.Reference migrationBinding.Source
equal "binding retains stable owner ID and revision" (migrationOwnerId, 2) (migrationBinding.OwnerId, migrationBinding.OwnerRevision)
equal "binding maps the explicit Return call to its structural AST path"
    (path [ FlowAstPathSegment.BlockStatement 0; FlowAstPathSegment.ReturnOutput 0 ])
    migrationBinding.Site.Path
equal "binding resolves the migrated call to the add primitive"
    (FlowLowering.FlowCallTargetIdentity.Primitive(PrimitiveId "add"))
    migrationBinding.Site.Target

let baseProgramData = VerifiedIrProgram.inspect baseMigrationProgram
let finalProgramData = VerifiedIrProgram.inspect migrated.Program
let baseCallerFunction = baseProgramData.FunctionsById[migrationCallerId]
let finalCallerFunction = finalProgramData.FunctionsById[migrationCallerId]
let baseCallerCalls = authoredResolvedCallsInBlock baseCallerFunction.FunctionBody
let finalCallerCalls = authoredResolvedCallsInBlock finalCallerFunction.FunctionBody

equal "retained caller's stack-authored entry is unchanged" stackCallerEntry migrated.Context.CompilerContext.Words[migrationCallerName]
equal "retained caller keeps its function revision" baseCallerFunction.FunctionRevision finalCallerFunction.FunctionRevision
equal "base caller IR targeted the original owner revision" [ UserWordTarget(migrationOwnerId, 1) ] (baseCallerCalls |> List.map (fun call -> call.ResolvedTarget))
equal "final IR retains caller and redirects it to the replacement revision"
    [ UserWordTarget(migrationOwnerId, 2) ]
    (finalCallerCalls |> List.map (fun call -> call.ResolvedTarget))
equal "unchanged stack caller executes the new Flow result" [ IntValue 42L ]
    (executeMigrationCaller migrated.Context migrated.Program "invoke-stack-after-migration")

let wrongHashFirstCharacter = if migrationSourceDocument.Reference.Hash[0] = '0' then '1' else '0'
let wrongHash = string wrongHashFirstCharacter + migrationSourceDocument.Reference.Hash.Substring(1)
let wrongHashDocument =
    { migrationSourceDocument with
        Reference = { migrationSourceDocument.Reference with Hash = wrongHash } }
let wrongHashChange = { migrationChange with Source = wrongHashDocument }
let baseOwnerBeforeHashFailure = migrationContext.CompilerContext.Words[migrationOwnerName]
let baseCallerBeforeHashFailure = migrationContext.CompilerContext.Words[migrationCallerName]

let wrongHashDiagnostic =
    try
        FlowLowering.compileBatchFlowSources migrationContext emptyBaseInventory [ wrongHashChange ] |> ignore
        failwith "wrong source hash was unexpectedly accepted"
    with
    | LanguageException diagnostic -> diagnostic

equal "wrong source hash is rejected with a structured diagnostic code" "FLOW_SOURCE_HASH_MISMATCH" wrongHashDiagnostic.Code
equal "wrong source hash rejection leaves the base owner unchanged" baseOwnerBeforeHashFailure migrationContext.CompilerContext.Words[migrationOwnerName]
equal "wrong source hash rejection leaves the base caller unchanged" baseCallerBeforeHashFailure migrationContext.CompilerContext.Words[migrationCallerName]
equal "base context remains executable after wrong hash rejection" [ IntValue 41L ]
    (executeMigrationCaller migrationContext baseMigrationProgram "invoke-stack-after-rejected-migration")

printfn "Flow call-binding probe passed %d assertions." assertions
