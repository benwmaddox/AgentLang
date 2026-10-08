module AgentLang.IR.Tests

open System
open AgentLang

let mutable private assertions = 0

let private check name condition =
    assertions <- assertions + 1
    if not condition then failwith $"{name}: assertion failed"

let private expectDiagnostic name code (action: unit -> unit) =
    try
        action ()
        failwith $"{name}: expected diagnostic {code}"
    with
    | LanguageException diagnostic when diagnostic.Code = code ->
        assertions <- assertions + 1
    | LanguageException diagnostic ->
        failwith $"{name}: expected {code}, got {diagnostic.Code}: {diagnostic.Message}"

let private expectDiagnosticRedactsMarkers name code (privateMarkers: SourceSpan list) (action: unit -> unit) =
    try
        action ()
        failwith $"{name}: expected diagnostic {code}"
    with
    | LanguageException diagnostic when diagnostic.Code = code ->
        assertions <- assertions + 1
        let rendered = sprintf "%A" diagnostic
        check $"{name} does not expose private marker coordinates"
            (privateMarkers |> List.forall (fun marker -> not (rendered.Contains(sprintf "%A" marker))))
    | LanguageException diagnostic ->
        failwith $"{name}: expected {code}, got {diagnostic.Code}: {diagnostic.Message}"

let private expectDiagnosticDetails name code span expected actual (action: unit -> unit) =
    try
        action ()
        failwith $"{name}: expected diagnostic {code}"
    with
    | LanguageException diagnostic when diagnostic.Code = code ->
        assertions <- assertions + 1
        check $"{name} preserves authored diagnostic details"
            (diagnostic.Span = span && diagnostic.Expected = expected && diagnostic.Actual = actual)
    | LanguageException diagnostic ->
        failwith $"{name}: expected {code}, got {diagnostic.Code}: {diagnostic.Message}"

let private noEffects = Set.empty<IrEffect>

let private sourceSpan file line =
    { File = file
      Line = line
      Column = 1
      Length = 1 }

let private noOpIrHost () : IrInterpreterHost =
    { PreflightEffects = fun _ _ _ -> ()
      ChargeInstruction = fun _ _ -> ()
      RecordBranchOutcome = fun _ _ _ -> ()
      RecordUse = ignore
      InvokeEffect = fun _ -> EffectUnit
      EnterUserFunction = fun _ _ _ -> fun () -> ()
      ReturnUserFunction = fun _ _ _ -> ()
      WordDefinitionSpan = fun _ -> None
      PrimitiveDefinitionSpan = fun _ -> None }

let private site owner ordinal = SourceSiteId(Some owner, ordinal)

let private source owner ordinal kind =
    let siteId = site owner ordinal
    siteId,
    { SiteOwner = Some owner
      SiteSpan = sourceSpan (sprintf "%A.agent" owner) (ordinal + 1)
      SourceKind = kind }

let private shape stack locals =
    { StackTypes = stack
      LocalTypes = locals }

let private block entryStack entryLocals code exitStack exitLocals =
    { EntryShape = shape entryStack entryLocals
      ExitShape = shape exitStack exitLocals
      Code = code }

let private instruction owner ordinal operation =
    { Site = site owner ordinal
      Operation = operation }

let private functionDefinition owner revision inputs outputs declared inferred localNames body =
    { FunctionId = owner
      FunctionRevision = revision
      FunctionName = sprintf "%A" owner
      InputTypes = inputs
      OutputTypes = outputs
      FunctionDeclaredEffects = declared
      FunctionInferredEffects = inferred
      LocalNames = localNames
      FunctionBody = body }

let private functionWithCode owner revision inputs outputs declared inferred localNames code =
    functionDefinition owner revision inputs outputs declared inferred localNames
        (block inputs Map.empty code outputs Map.empty)

let private coverage owner instructionOrdinals branchCases =
    { CoveredSites = instructionOrdinals |> List.map (site owner) |> Set.ofList
      BranchOutcomes = branchCases |> List.map (fun (ordinal, outcomes) -> site owner ordinal, outcomes) |> Map.ofList }

let private program functions generated nominalTypes sourceMap coverageByWord =
    { NominalTypesByKey = Map.ofList nominalTypes
      FunctionsById = Map.ofList functions
      GeneratedTargetsById = Map.ofList generated
      SourceMap = Map.ofList sourceMap
      CoverageByWord = Map.ofList coverageByWord }

let private testSpan file line =
    { File = file; Line = line; Column = 1; Length = 1 }

let private wordEntry name inputs outputs effects body revision status =
    let span = testSpan (name + ".agent") 1
    let definition =
        { Name = name
          Inputs = inputs
          Outputs = outputs
          Effects = effects
          Maturity = LibraryWord
          Revision = revision
          Documentation = "IR lowering test word."
          Body = body
          SourceText = "same-authoring-text"
          Span = span }
    { Definition = definition
      Builtin = None
      Status = status
      Maturity = LibraryWord
      Revision = revision }

let private generatedEntry name builtin inputs outputs =
    let definition =
        { Name = name
          Inputs = inputs
          Outputs = outputs
          Effects = Set.empty
          Maturity = LibraryWord
          Revision = 1
          Documentation = "Generated test word."
          Body = []
          SourceText = "generated"
          Span = testSpan (name + ".agent") 1 }
    { Definition = definition
      Builtin = Some builtin
      Status = Persistent
      Maturity = LibraryWord
      Revision = 1 }

let private loweringContext
    (words: Map<string, WordEntry>)
    (records: Map<string, RecordDefinition>)
    (scalars: Map<string, ScalarTypeDefinition>)
    : Compiler.IrLoweringContext =
    let allWords =
        Map.fold (fun found name entry -> Map.add name entry found) Compiler.primitives words
    let identity name entry =
        let prefix =
            match entry.Builtin with
            | Some(BuiltinOp _) -> "primitive-"
            | Some _ -> "generated-"
            | None -> "user-"
        name, WordId(prefix + name)
    let ids = allWords |> Map.toList |> List.map (fun (name, entry) -> identity name entry) |> Map.ofList
    { Words = allWords
      Records = records
      Scalars = scalars
      Enums = Map.empty
      WordIds = ids }

let private resolved target name inputs outputs effects =
    { ResolvedTarget = target
      ResolvedName = name
      InputTypes = inputs
      OutputTypes = outputs
      ResolvedDeclaredEffects = effects
      ResolvedEffects = effects }

let private primitiveContract id inputs outputs effects =
    let primitive = PrimitiveId id
    primitive,
    { Primitive = primitive
      InputPatterns = inputs
      OutputPatterns = outputs
      PrimitiveEffects = effects }

let private verify catalog executable =
    IrVerifier.verify catalog executable |> ignore

let private testClosedEffects () =
    let cases =
        [ IrEffect.FileRead, "fs.read"
          IrEffect.FileWrite, "fs.write"
          IrEffect.DatabaseRead, "db.read"
          IrEffect.DatabaseWrite, "db.write"
          IrEffect.NetworkRead, "network.read"
          IrEffect.NetworkWrite, "network.write"
          IrEffect.ProcessExecute, "process.execute"
          IrEffect.ClockRead, "clock.read"
          IrEffect.RandomRead, "random.read"
          IrEffect.ConsoleWrite, "console.write" ]
    for effect, name in cases do
        check $"closed effect round-trips {name}" (IrEffects.format effect = name && IrEffects.ofName name = Some effect)
    check "unknown source effect cannot enter closed IR through the mapper" (IrEffects.ofName "reflection.invoke" = None)
    check "effect formatting is stable and sorted by source name" (IrEffects.names (cases |> List.map fst |> Set.ofList) = (cases |> List.map snd |> List.sort))

let private testPrimitiveSpecializations () =
    let equalsId, equalsContract =
        primitiveContract "equals" [ PatternVariable 0; PatternVariable 0 ] [ PatternBool ] noEffects
    let catalog = Map.ofList [ equalsId, equalsContract ]
    let intWord = WordId "equal-ints"
    let stringWord = WordId "equal-strings"
    let intSite, intSource = source intWord 0 "call"
    let stringSite, stringSource = source stringWord 0 "call"
    let intCall = resolved (PrimitiveTarget equalsId) "equals" [ IrInt; IrInt ] [ IrBool ] noEffects
    let stringCall = resolved (PrimitiveTarget equalsId) "equals" [ IrString; IrString ] [ IrBool ] noEffects
    let intFunction =
        functionWithCode intWord 0 [ IrInt; IrInt ] [ IrBool ] noEffects noEffects Map.empty
            [ { Site = intSite; Operation = IrOperation.Call intCall } ]
    let stringFunction =
        functionWithCode stringWord 0 [ IrString; IrString ] [ IrBool ] noEffects noEffects Map.empty
            [ { Site = stringSite; Operation = IrOperation.Call stringCall } ]
    let executable =
        program
            [ intWord, intFunction; stringWord, stringFunction ]
            [] []
            [ intSite, intSource; stringSite, stringSource ]
            [ intWord, coverage intWord [ 0 ] []; stringWord, coverage stringWord [ 0 ] [] ]
    let verified = IrVerifier.verify catalog executable
    check "one primitive supports independent Int and String call-site instances" (VerifiedIrProgram.inspect verified = executable)
    check "public model verification does not authorize backend execution" (not (VerifiedIrProgram.isBackendExecutable verified))
    expectDiagnostic "verifier-only snapshot is rejected before backend dispatch" "IR_BACKEND_UNTRUSTED_PROGRAM" (fun () -> VerifiedIrProgram.requireBackendRegistry catalog verified)

    let malformedCall = { intCall with InputTypes = [ IrInt; IrString ] }
    let malformedFunction = { intFunction with FunctionBody = block [ IrInt; IrInt ] Map.empty [ { Site = intSite; Operation = IrOperation.Call malformedCall } ] [ IrBool ] Map.empty }
    let malformed = { executable with FunctionsById = executable.FunctionsById.Add(intWord, malformedFunction) }
    expectDiagnostic "primitive call rejects mismatched type-variable instantiation" "IR_CALL_SIGNATURE_MISMATCH" (fun () -> verify catalog malformed)

    let unboundOutputId, unboundOutput =
        primitiveContract "bad-polymorphic" [ PatternInt ] [ PatternVariable 2 ] noEffects
    expectDiagnostic "primitive contract cannot invent an output type" "IR_PRIMITIVE_CONTRACT_VARIABLE" (fun () -> verify (Map.ofList [ unboundOutputId, unboundOutput ]) executable)

let private testClosedContainerConstructors () =
    let noneWord = WordId "none-int"
    let emptyWord = WordId "empty-nested"
    let someWord = WordId "some-string"
    let singletonWord = WordId "singleton-int"
    let errorWord = WordId "result-error"
    let okWord = WordId "result-ok"
    let resultType = IrResult(IrList IrInt, IrString)
    let noneSite, noneSource = source noneWord 0 "option-none"
    let emptySite, emptySource = source emptyWord 0 "list-empty"
    let someSite, someSource = source someWord 0 "option-some"
    let singletonSite, singletonSource = source singletonWord 0 "list-singleton"
    let errorSite, errorSource = source errorWord 0 "result-error"
    let okSite, okSource = source okWord 0 "result-ok"
    let functions =
        [ noneWord,
          functionWithCode noneWord 1 [] [ IrOption IrInt ] noEffects noEffects Map.empty
            [ { Site = noneSite; Operation = IrOperation.OptionNone IrInt } ]
          emptyWord,
          functionWithCode emptyWord 1 [] [ IrList resultType ] noEffects noEffects Map.empty
            [ { Site = emptySite; Operation = IrOperation.ListEmpty resultType } ]
          someWord,
          functionWithCode someWord 1 [ IrString ] [ IrOption IrString ] noEffects noEffects Map.empty
            [ { Site = someSite; Operation = IrOperation.OptionSome IrString } ]
          singletonWord,
          functionWithCode singletonWord 1 [ IrInt ] [ IrList IrInt ] noEffects noEffects Map.empty
            [ { Site = singletonSite; Operation = IrOperation.ListSingleton IrInt } ]
          errorWord,
          functionWithCode errorWord 1 [ IrString ] [ IrResult(IrList IrInt, IrString) ] noEffects noEffects Map.empty
            [ { Site = errorSite; Operation = IrOperation.ResultError(IrList IrInt, IrString) } ]
          okWord,
          functionWithCode okWord 1 [ IrList IrInt ] [ IrResult(IrList IrInt, IrString) ] noEffects noEffects Map.empty
            [ { Site = okSite; Operation = IrOperation.ResultOk(IrList IrInt, IrString) } ] ]
    let executable =
        program functions [] []
            [ noneSite, noneSource; emptySite, emptySource; someSite, someSource; singletonSite, singletonSource
              errorSite, errorSource; okSite, okSource ]
            [ noneWord, coverage noneWord [ 0 ] []
              emptyWord, coverage emptyWord [ 0 ] []
              someWord, coverage someWord [ 0 ] []
              singletonWord, coverage singletonWord [ 0 ] []
              errorWord, coverage errorWord [ 0 ] []
              okWord, coverage okWord [ 0 ] [] ]
    verify Map.empty executable
    check "typed empty and active container constructors retain closed type arguments" true

    let badWord = WordId "bad-list-payload"
    let badSite, badSource = source badWord 0 "list-singleton"
    let badFunction =
        functionWithCode badWord 1 [ IrString ] [ IrList IrInt ] noEffects noEffects Map.empty
            [ { Site = badSite; Operation = IrOperation.ListSingleton IrInt } ]
    let badProgram =
        program [ badWord, badFunction ] [] [] [ badSite, badSource ] [ badWord, coverage badWord [ 0 ] [] ]
    expectDiagnostic "container constructor rejects a mismatched payload" "IR_CONTAINER_PAYLOAD" (fun () -> verify Map.empty badProgram)

    let unknown = IrNominal(ProgramTypeKey 91)
    let unknownWord = WordId "unknown-nominal"
    let unknownFunction = functionWithCode unknownWord 0 [ IrList unknown ] [] noEffects noEffects Map.empty []
    let unknownProgram = program [ unknownWord, unknownFunction ] [] [] [] [ unknownWord, coverage unknownWord [] [] ]
    expectDiagnostic "function signature rejects unresolved nominal keys" "IR_UNKNOWN_TYPE_KEY" (fun () -> verify Map.empty unknownProgram)

    let verifyInvalidConstant name literal ty =
        let word = WordId name
        let literalSite, literalSource = source word 0 "literal"
        let fn =
            functionWithCode word 1 [] [ ty ] noEffects noEffects Map.empty
                [ { Site = literalSite; Operation = IrOperation.Constant(literal, ty) } ]
        let malformed =
            program [ word, fn ] [] [] [ literalSite, literalSource ] [ word, coverage word [ 0 ] [] ]
        expectDiagnostic (name + " is rejected by the verified IR boundary") "IR_CONSTANT_LITERAL_INVALID" (fun () -> verify Map.empty malformed)

    verifyInvalidConstant "nan-constant" (LFloat Double.NaN) IrFloat
    verifyInvalidConstant "infinite-constant" (LFloat Double.PositiveInfinity) IrFloat
    verifyInvalidConstant "null-string-constant" (LString null) IrString

    let hostContext = loweringContext Map.empty Map.empty Map.empty
    expectDiagnostic "host AST compilation rejects nonfinite constants before backend authority" "IR_CONSTANT_LITERAL_INVALID" (fun () ->
        Compiler.compileIrBody hostContext "host-nan" [] [ Push(LFloat Double.NaN, testSpan "host.agent" 1) ] |> ignore)

let private testStructuredBranchesAndCoverage () =
    let chooseWord = WordId "choose-int"
    let conditionSite, conditionSource = source chooseWord 0 "if"
    let thenSite, thenSource = source chooseWord 1 "literal"
    let elseSite, elseSource = source chooseWord 2 "literal"
    let thenBlock = block [] Map.empty [ { Site = thenSite; Operation = IrOperation.Constant(LInt 10L, IrInt) } ] [ IrInt ] Map.empty
    let elseBlock = block [] Map.empty [ { Site = elseSite; Operation = IrOperation.Constant(LInt 20L, IrInt) } ] [ IrInt ] Map.empty
    let functionBody = block [ IrBool ] Map.empty [ { Site = conditionSite; Operation = IrOperation.If(thenBlock, elseBlock) } ] [ IrInt ] Map.empty
    let choose = functionDefinition chooseWord 1 [ IrBool ] [ IrInt ] noEffects noEffects Map.empty functionBody
    let executable =
        program [ chooseWord, choose ] [] []
            [ conditionSite, conditionSource; thenSite, thenSource; elseSite, elseSource ]
            [ chooseWord, coverage chooseWord [ 0; 1; 2 ] [ 0, [ "true"; "false" ] ] ]
    verify Map.empty executable
    check "if branches verify equal stack/local joins and retain both coverage outcomes" true

    let missingOutcome = { executable with CoverageByWord = Map.add chooseWord (coverage chooseWord [ 0; 1; 2 ] [ 0, [ "true" ] ]) executable.CoverageByWord }
    expectDiagnostic "library coverage cannot omit an if outcome" "IR_COVERAGE_MAP_MISMATCH" (fun () -> verify Map.empty missingOutcome)

    let badElse = block [] Map.empty [] [] Map.empty
    let badBody = block [ IrBool ] Map.empty [ { Site = conditionSite; Operation = IrOperation.If(thenBlock, badElse) } ] [ IrInt ] Map.empty
    let badFunction = { choose with FunctionBody = badBody }
    let badProgram =
        { executable with
            FunctionsById = Map.add chooseWord badFunction executable.FunctionsById
            SourceMap = Map.remove elseSite executable.SourceMap
            CoverageByWord = Map.add chooseWord (coverage chooseWord [ 0; 1 ] [ 0, [ "true"; "false" ] ]) executable.CoverageByWord }
    expectDiagnostic "if branches with different result stacks are rejected" "IR_BRANCH_JOIN_MISMATCH" (fun () -> verify Map.empty badProgram)

    let duplicateElse = block [] Map.empty [ { Site = thenSite; Operation = IrOperation.Constant(LInt 20L, IrInt) } ] [ IrInt ] Map.empty
    let duplicateBody = block [ IrBool ] Map.empty [ { Site = conditionSite; Operation = IrOperation.If(thenBlock, duplicateElse) } ] [ IrInt ] Map.empty
    let duplicateFunction = { choose with FunctionBody = duplicateBody }
    let duplicateProgram =
        { executable with
            FunctionsById = Map.add chooseWord duplicateFunction executable.FunctionsById
            SourceMap = Map.remove elseSite executable.SourceMap
            CoverageByWord = Map.add chooseWord (coverage chooseWord [ 0; 1 ] [ 0, [ "true"; "false" ] ]) executable.CoverageByWord }
    expectDiagnostic "nested branch instructions cannot reuse source-site identities" "IR_DUPLICATE_SOURCE_SITE" (fun () -> verify Map.empty duplicateProgram)

let private testClosedSyntheticSourceKinds () =
    let owner = WordId "synthetic-scope-owner"
    let scopeSite, scopeSource = source owner 0 "synthetic-scope"
    let childSite, childSource = source owner 1 "constant"
    let child =
        block [] Map.empty
            [ { Site = childSite; Operation = IrOperation.Constant(LInt 7L, IrInt) } ]
            [ IrInt ] Map.empty
    let body = block [] Map.empty [ { Site = scopeSite; Operation = IrOperation.Scope child } ] [ IrInt ] Map.empty
    let functionValue = functionDefinition owner 1 [] [ IrInt ] noEffects noEffects Map.empty body
    let valid =
        program [ owner, functionValue ] [] []
            [ scopeSite, scopeSource; childSite, childSource ]
            [ owner, coverage owner [ 1 ] [] ]
    verify Map.empty valid
    check "Scope classification is exact and the nested authored instruction retains coverage" true

    let constantFunction site =
        let body = block [] Map.empty [ { Site = site; Operation = IrOperation.Constant(LInt 1L, IrInt) } ] [ IrInt ] Map.empty
        functionDefinition owner 1 [] [ IrInt ] noEffects noEffects Map.empty body
    let scopeSpoofSource = { childSource with SourceKind = "synthetic-scope" }
    let spoofedOpcode =
        program [ owner, constantFunction childSite ] [] [] [ childSite, scopeSpoofSource ]
            [ owner, coverage owner [] [] ]
    expectDiagnostic "synthetic Scope cannot hide a real constant instruction" "IR_SYNTHETIC_SOURCE_KIND_INVALID" (fun () -> verify Map.empty spoofedOpcode)

    let unknownSource = { childSource with SourceKind = "synthetic-unreviewed" }
    let unknownKind =
        program [ owner, constantFunction childSite ] [] [] [ childSite, unknownSource ]
            [ owner, coverage owner [ 1 ] [] ]
    expectDiagnostic "unknown synthetic classifications fail closed" "IR_SYNTHETIC_SOURCE_KIND_INVALID" (fun () -> verify Map.empty unknownKind)

    let verified = Compiler.compileIrProgram (loweringContext Map.empty Map.empty Map.empty)
    let detachedSite = SourceSiteId(None, 0)
    let detachedSource = { childSource with SiteOwner = None; SourceKind = "synthetic-store-local" }
    let detachedBlock =
        block [] Map.empty [ { Site = detachedSite; Operation = IrOperation.Constant(LInt 1L, IrInt) } ] [ IrInt ] Map.empty
    let detachedBody =
        { BodyName = "synthetic-source-kind"
          BodyInputTypes = []
          BodyOutputTypes = [ IrInt ]
          BodyDeclaredEffects = noEffects
          BodyInferredEffects = noEffects
          BodyLocalNames = Map.empty
          BodyBlock = detachedBlock
          BodySourceMap = Map.ofList [ detachedSite, { detachedSource with SiteSpan = testSpan "detached.agent" 1 } ]
          BodyCoverage = { CoveredSites = Set.empty; BranchOutcomes = Map.empty } }
    expectDiagnostic "detached body verifier applies the same closed opcode/source contract" "IR_SYNTHETIC_SOURCE_KIND_INVALID" (fun () -> IrVerifier.verifyBody verified detachedBody |> ignore)

let private testOptionAndResultCaseLocals () =
    let optionWord = WordId "option-value"
    let optionSome = LocalSlot 0
    let optionSite, optionSource = source optionWord 0 "match-option"
    let someSite, someSource = source optionWord 1 "option-some-case"
    let noneSite, noneSource = source optionWord 2 "option-none-case"
    let someBlock =
        block [] (Map.ofList [ optionSome, IrInt ])
            [ { Site = someSite; Operation = IrOperation.LoadLocal optionSome } ]
            [ IrInt ] (Map.ofList [ optionSome, IrInt ])
    let noneBlock =
        block [] Map.empty [ { Site = noneSite; Operation = IrOperation.Constant(LInt 0L, IrInt) } ] [ IrInt ] Map.empty
    let optionBody =
        block [ IrOption IrInt ] Map.empty
            [ { Site = optionSite; Operation = IrOperation.MatchOption(optionSome, someBlock, noneBlock) } ]
            [ IrInt ] Map.empty
    let optionFunction = functionDefinition optionWord 1 [ IrOption IrInt ] [ IrInt ] noEffects noEffects (Map.ofList [ optionSome, "value" ]) optionBody

    let resultWord = WordId "result-value"
    let okLocal = LocalSlot 1
    let errorLocal = LocalSlot 2
    let resultSite, resultSource = source resultWord 0 "match-result"
    let okSite, okSource = source resultWord 1 "result-ok-case"
    let errorSite, errorSource = source resultWord 2 "result-error-case"
    let okBlock =
        block [] (Map.ofList [ okLocal, IrInt ])
            [ { Site = okSite; Operation = IrOperation.LoadLocal okLocal } ]
            [ IrInt ] (Map.ofList [ okLocal, IrInt ])
    let errorBlock =
        block [] (Map.ofList [ errorLocal, IrString ])
            [ { Site = errorSite; Operation = IrOperation.Constant(LInt 1L, IrInt) } ]
            [ IrInt ] (Map.ofList [ errorLocal, IrString ])
    let resultType = IrResult(IrInt, IrString)
    let resultBody =
        block [ resultType ] Map.empty
            [ { Site = resultSite; Operation = IrOperation.MatchResult(okLocal, errorLocal, okBlock, errorBlock) } ]
            [ IrInt ] Map.empty
    let resultFunction =
        functionDefinition resultWord 1 [ resultType ] [ IrInt ] noEffects noEffects
            (Map.ofList [ okLocal, "value"; errorLocal, "error" ]) resultBody

    let executable =
        program [ optionWord, optionFunction; resultWord, resultFunction ] [] []
            [ optionSite, optionSource; someSite, someSource; noneSite, noneSource
              resultSite, resultSource; okSite, okSource; errorSite, errorSource ]
            [ optionWord, coverage optionWord [ 0; 1; 2 ] [ 0, [ "some"; "none" ] ]
              resultWord, coverage resultWord [ 0; 1; 2 ] [ 0, [ "ok"; "error" ] ] ]
    verify Map.empty executable
    check "Option and Result payload locals are case-local and stripped at the join" true

    let localWord = WordId "preserved-outer-local"
    let outerLocal = LocalSlot 3
    let payloadLocal = LocalSlot 4
    let storeSite, storeSource = source localWord 0 "store-local"
    let matchSite, matchSource = source localWord 1 "match-option"
    let localSomeSite, localSomeSource = source localWord 2 "option-some-case"
    let localNoneSite, localNoneSource = source localWord 3 "option-none-case"
    let localSome =
        block [] (Map.ofList [ outerLocal, IrInt; payloadLocal, IrInt ])
            [ { Site = localSomeSite; Operation = IrOperation.LoadLocal payloadLocal } ]
            [ IrInt ] (Map.ofList [ outerLocal, IrInt; payloadLocal, IrInt ])
    let localNone =
        block [] (Map.ofList [ outerLocal, IrInt ])
            [ { Site = localNoneSite; Operation = IrOperation.LoadLocal outerLocal } ]
            [ IrInt ] (Map.ofList [ outerLocal, IrInt ])
    let localBody =
        block [ IrOption IrInt; IrInt ] Map.empty
            [ { Site = storeSite; Operation = IrOperation.StoreLocal outerLocal }
              { Site = matchSite; Operation = IrOperation.MatchOption(payloadLocal, localSome, localNone) } ]
            [ IrInt ] (Map.ofList [ outerLocal, IrInt ])
    let localFunction =
        functionDefinition localWord 1 [ IrOption IrInt; IrInt ] [ IrInt ] noEffects noEffects
            (Map.ofList [ outerLocal, "outer"; payloadLocal, "value" ]) localBody
    let localProgram =
        program [ localWord, localFunction ] [] []
            [ storeSite, storeSource; matchSite, matchSource; localSomeSite, localSomeSource; localNoneSite, localNoneSource ]
            [ localWord, coverage localWord [ 0; 1; 2; 3 ] [ 1, [ "some"; "none" ] ] ]
    verify Map.empty localProgram
    check "store/load locals preserve outer locals through Option case joins" true

    let shadowWord = WordId "option-shadow"
    let shadowSite, shadowSource = source shadowWord 0 "store-local"
    let shadowMatchSite, shadowMatchSource = source shadowWord 1 "match-option"
    let shadowNoneSite, shadowNoneSource = source shadowWord 3 "option-none-case"
    let shadowSomeBlock = block [] (Map.ofList [ optionSome, IrInt ]) [] [] (Map.ofList [ optionSome, IrInt ])
    let shadowNoneBlock = block [] Map.empty [] [] Map.empty
    let shadowBody =
        block [ IrOption IrInt; IrInt ] Map.empty
            [ { Site = shadowSite; Operation = IrOperation.StoreLocal optionSome }
              { Site = shadowMatchSite; Operation = IrOperation.MatchOption(optionSome, shadowSomeBlock, shadowNoneBlock) } ]
            [] (Map.ofList [ optionSome, IrInt ])
    let shadowFunction = functionDefinition shadowWord 1 [ IrOption IrInt; IrInt ] [] noEffects noEffects (Map.ofList [ optionSome, "outer" ]) shadowBody
    let shadowProgram =
        program [ shadowWord, shadowFunction ] [] []
            [ shadowSite, shadowSource; shadowMatchSite, shadowMatchSource
              shadowNoneSite, shadowNoneSource ]
            [ shadowWord, coverage shadowWord [ 0; 1 ] [ 1, [ "some"; "none" ] ] ]
    expectDiagnostic "case payload cannot shadow an initialized outer local" "IR_CASE_LOCAL_SHADOW" (fun () -> verify Map.empty shadowProgram)

let private testClosedEnumVerification () =
    let enumKey = ProgramTypeKey 0
    let otherEnumKey = ProgramTypeKey 1
    let recordKey = ProgramTypeKey 2
    let enumType = IrNominal enumKey
    let enumDefinition =
        IrEnumDefinition
            { TypeKey = enumKey
              TypeName = "Color"
              Cases = [ "red"; "green"; "blue" ] }
    let otherEnumDefinition =
        IrEnumDefinition
            { TypeKey = otherEnumKey
              TypeName = "Shade"
              Cases = [ "light"; "dark" ] }
    let recordDefinition =
        IrRecordDefinition
            { TypeKey = recordKey
              TypeName = "Point"
              RecordFields = [] }
    let nominalTypes =
        [ enumKey, enumDefinition
          otherEnumKey, otherEnumDefinition
          recordKey, recordDefinition ]
    let caseNames = [ "red"; "green"; "blue" ]
    let caseIndexes = [ 0 .. caseNames.Length - 1 ]
    let targetRows =
        caseNames
        |> List.mapi (fun caseIndex caseName ->
            let targetId = WordId("generated-color-" + caseName)
            let targetSite, targetSource = source targetId 0 "enum-case-declaration"
            let target =
                { TargetId = targetId
                  TargetRevision = 1
                  TargetName = "Color." + caseName
                  Operation = MakeEnumCaseOperation(enumKey, caseIndex)
                  InputTypes = []
                  OutputTypes = [ enumType ]
                  TargetDeclaredEffects = noEffects
                  TargetEffects = noEffects
                  SourceSite = Some targetSite }
            caseIndex, target, targetSource)
    let constructorRows =
        targetRows
        |> List.map (fun (caseIndex, target, targetSource) ->
            let owner = WordId("construct-color-" + caseNames[caseIndex])
            let callSite, callSource = source owner 0 "enum-case-constructor"
            let call =
                resolved
                    (GeneratedWordTarget(target.TargetId, target.TargetRevision))
                    target.TargetName [] [ enumType ] noEffects
            let fn =
                functionWithCode owner 1 [] [ enumType ] noEffects noEffects Map.empty
                    [ { Site = callSite
                        Operation = IrOperation.MakeEnumCase(call, enumKey, caseIndex) } ]
            owner, fn, callSource, targetSource)

    let matchWord = WordId "match-color"
    let matchSite, matchSource = source matchWord 0 "match-enum"
    let matchLocal = LocalSlot 0
    let otherLocal = LocalSlot 1
    let matchLocalTypes = Map.ofList [ matchLocal, IrInt ]
    let matchBranches =
        caseIndexes
        |> List.map (fun caseIndex ->
            let baseOrdinal = 1 + caseIndex * 3
            let constantSite, constantSource = source matchWord baseOrdinal "enum-case-value"
            let storeSite, storeSource = source matchWord (baseOrdinal + 1) "store-local"
            let outputSite, outputSource = source matchWord (baseOrdinal + 2) "enum-case-value"
            let branch =
                block [] Map.empty
                    [ { Site = constantSite; Operation = IrOperation.Constant(LInt(int64 (caseIndex + 1)), IrInt) }
                      { Site = storeSite; Operation = IrOperation.StoreLocal matchLocal }
                      { Site = outputSite; Operation = IrOperation.Constant(LInt(int64 ((caseIndex + 1) * 10)), IrInt) } ]
                    [ IrInt ] matchLocalTypes
            caseIndex, branch, [ constantSource; storeSource; outputSource ])
    let matchBody =
        block [ enumType ] Map.empty
            [ { Site = matchSite
                Operation = IrOperation.MatchEnum(enumKey, matchBranches |> List.map (fun (index, branch, _) -> index, branch)) } ]
            [ IrInt ] matchLocalTypes
    let matchFunction =
        functionDefinition matchWord 1 [ enumType ] [ IrInt ] noEffects noEffects
            (Map.ofList [ matchLocal, "seen"; otherLocal, "other" ]) matchBody
    let constructorFunctions = constructorRows |> List.map (fun (owner, fn, _, _) -> owner, fn)
    let sourceEntries =
        [ yield matchSite, matchSource
          for owner, _, callSource, _ in constructorRows do
              yield site owner 0, callSource
          for _, target, targetSource in targetRows do
              yield target.SourceSite.Value, targetSource
          for caseIndex, _, branchSources in matchBranches do
              for offset, branchSource in List.indexed branchSources do
                  yield site matchWord (1 + caseIndex * 3 + offset), branchSource ]
    let branchCasePairs = matchBranches |> List.map (fun (index, branch, _) -> index, branch)
    let matchCoverage = coverage matchWord [ 0 .. 9 ] [ 0, caseNames ]
    let constructorCoverages =
        constructorRows |> List.map (fun (owner, _, _, _) -> owner, coverage owner [ 0 ] [])
    let executable =
        program
            (constructorFunctions @ [ matchWord, matchFunction ])
            (targetRows |> List.map (fun (_, target, _) -> target.TargetId, target))
            nominalTypes sourceEntries
            (constructorCoverages @ [ matchWord, matchCoverage ])

    verify Map.empty executable
    check "each generated enum constructor is bound to its frozen case index" (
        targetRows
        |> List.forall (fun (caseIndex, target, _) ->
            target.Operation = MakeEnumCaseOperation(enumKey, caseIndex)
            && target.TargetName = "Color." + caseNames[caseIndex]
            && target.InputTypes = []
            && target.OutputTypes = [ enumType ]))

    let targetAt caseIndex = targetRows |> List.find (fun (index, _, _) -> index = caseIndex) |> fun (_, target, _) -> target
    let replaceTarget caseIndex mutate =
        let target = targetAt caseIndex
        { executable with GeneratedTargetsById = Map.add target.TargetId (mutate target) executable.GeneratedTargetsById }
    expectDiagnostic "generated enum constructor rejects a different frozen case identity" "IR_GENERATED_SIGNATURE_MISMATCH" (fun () ->
        verify Map.empty (replaceTarget 0 (fun target -> { target with Operation = MakeEnumCaseOperation(enumKey, 1) })))
    expectDiagnostic "generated enum constructor requires zero inputs" "IR_GENERATED_SIGNATURE_MISMATCH" (fun () ->
        verify Map.empty (replaceTarget 0 (fun target -> { target with InputTypes = [ IrInt ] })))
    expectDiagnostic "generated enum constructor rejects an out-of-range case index" "IR_GENERATED_SIGNATURE_MISMATCH" (fun () ->
        verify Map.empty (replaceTarget 0 (fun target -> { target with Operation = MakeEnumCaseOperation(enumKey, caseNames.Length) })))
    expectDiagnostic "generated enum constructor rejects a different nominal enum key" "IR_GENERATED_SIGNATURE_MISMATCH" (fun () ->
        verify Map.empty (replaceTarget 0 (fun target -> { target with Operation = MakeEnumCaseOperation(otherEnumKey, 0) })))
    expectDiagnostic "generated enum constructor rejects a non-enum nominal key" "IR_GENERATED_SIGNATURE_MISMATCH" (fun () ->
        verify Map.empty (replaceTarget 0 (fun target -> { target with Operation = MakeEnumCaseOperation(recordKey, 0) })))
    expectDiagnostic "generated enum constructor output must retain its exact nominal key" "IR_GENERATED_SIGNATURE_MISMATCH" (fun () ->
        verify Map.empty (replaceTarget 0 (fun target -> { target with OutputTypes = [ IrNominal otherEnumKey ] })))
    expectDiagnostic "generated enum constructor name must match its frozen case" "IR_GENERATED_SIGNATURE_MISMATCH" (fun () ->
        verify Map.empty (replaceTarget 0 (fun target -> { target with TargetName = "Color.green" })))

    let redOwner, redFunction = constructorFunctions[0]
    let blueTarget = targetAt 2
    let blueCall = resolved (GeneratedWordTarget(blueTarget.TargetId, blueTarget.TargetRevision)) blueTarget.TargetName [] [ enumType ] noEffects
    let redInstruction = redFunction.FunctionBody.Code.Head
    let wrongConstructorIdentity =
        { redFunction with
            FunctionBody =
                { redFunction.FunctionBody with
                    Code = [ { redInstruction with Operation = IrOperation.MakeEnumCase(blueCall, enumKey, 0) } ] } }
    expectDiagnostic "enum construction operation must use the target for the exact case" "IR_GENERATED_OPERATION_MISMATCH" (fun () ->
        verify Map.empty { executable with FunctionsById = Map.add redOwner wrongConstructorIdentity executable.FunctionsById })

    let withMatchCases cases =
        let changedBody =
            { matchFunction.FunctionBody with
                Code = [ { Site = matchSite; Operation = IrOperation.MatchEnum(enumKey, cases) } ] }
        let changedSites =
            seq {
                yield matchSite
                for _, branch in cases do
                    yield! branch.Code |> Seq.map (fun instruction -> instruction.Site)
            }
            |> Set.ofSeq
        let changedCoverage =
            { CoveredSites = changedSites
              BranchOutcomes = Map.ofList [ matchSite, caseNames ] }
        { executable with
            FunctionsById = Map.add matchWord { matchFunction with FunctionBody = changedBody } executable.FunctionsById
            CoverageByWord = Map.add matchWord changedCoverage executable.CoverageByWord }
    let branchAt index = branchCasePairs |> List.find (fun (caseIndex, _) -> caseIndex = index) |> snd
    expectDiagnostic "enum match rejects a missing case index" "IR_ENUM_MATCH_CASE_SET" (fun () ->
        verify Map.empty (withMatchCases [ 0, branchAt 0; 1, branchAt 1 ]))
    expectDiagnostic "enum match rejects a duplicate case index" "IR_ENUM_MATCH_CASE_SET" (fun () ->
        verify Map.empty (withMatchCases [ 0, branchAt 0; 1, branchAt 1; 1, branchAt 1 ]))
    expectDiagnostic "enum match rejects an extra case index" "IR_ENUM_MATCH_CASE_SET" (fun () ->
        verify Map.empty (withMatchCases [ 0, branchAt 0; 1, branchAt 1; 2, branchAt 2; 3, branchAt 2 ]))
    expectDiagnostic "enum match rejects a negative case index" "IR_ENUM_MATCH_CASE_SET" (fun () ->
        verify Map.empty (withMatchCases [ -1, branchAt 0; 1, branchAt 1; 2, branchAt 2 ]))

    let wrongScrutineeFunction =
        { matchFunction with
            InputTypes = [ IrNominal otherEnumKey ]
            FunctionBody = { matchFunction.FunctionBody with EntryShape = shape [ IrNominal otherEnumKey ] Map.empty } }
    expectDiagnostic "enum match requires the exact nominal scrutinee key" "IR_ENUM_MATCH_TYPE" (fun () ->
        verify Map.empty { executable with FunctionsById = Map.add matchWord wrongScrutineeFunction executable.FunctionsById })

    let stackMismatchBlock =
        let branch = branchAt 1
        { branch with
            Code =
                branch.Code
                |> List.mapi (fun index instruction ->
                    if index = 2 then { instruction with Operation = IrOperation.Constant(LString "wrong", IrString) }
                    else instruction)
            ExitShape = shape [ IrString ] matchLocalTypes }
    expectDiagnostic "enum match arms must join with one exact output stack" "IR_BRANCH_JOIN_MISMATCH" (fun () ->
        verify Map.empty (withMatchCases [ 0, branchAt 0; 1, stackMismatchBlock; 2, branchAt 2 ]))

    let localMismatchBlock =
        let branch = branchAt 1
        { branch with
            Code =
                branch.Code
                |> List.mapi (fun index instruction ->
                    if index = 1 then { instruction with Operation = IrOperation.StoreLocal otherLocal }
                    else instruction)
            ExitShape = shape [ IrInt ] (Map.ofList [ otherLocal, IrInt ]) }
    expectDiagnostic "enum match arms must join with the same outer locals" "IR_BRANCH_JOIN_MISMATCH" (fun () ->
        verify Map.empty (withMatchCases [ 0, branchAt 0; 1, localMismatchBlock; 2, branchAt 2 ]))

    let effectId, effectContract = primitiveContract "enum.effect" [] [] (Set.singleton IrEffect.ConsoleWrite)
    let effectWord = WordId "enum-effect-join"
    let effectMatchSite, effectMatchSource = source effectWord 0 "match-enum"
    let effectCallSite, effectCallSource = source effectWord 1 "call"
    let effectValueSite, effectValueSource = source effectWord 2 "enum-case-value"
    let otherValueSites = [ 3 .. 4 ] |> List.map (fun ordinal -> let siteId, sourceEntry = source effectWord ordinal "enum-case-value" in ordinal, siteId, sourceEntry)
    let effectCall = resolved (PrimitiveTarget effectId) "enum.effect" [] [] (Set.singleton IrEffect.ConsoleWrite)
    let effectBranches =
        [ 0,
          block [] Map.empty
            [ { Site = effectCallSite; Operation = IrOperation.Call effectCall }
              { Site = effectValueSite; Operation = IrOperation.Constant(LInt 10L, IrInt) } ]
            [ IrInt ] Map.empty
          1,
          block [] Map.empty
            [ { Site = (otherValueSites[0] |> fun (_, siteId, _) -> siteId); Operation = IrOperation.Constant(LInt 20L, IrInt) } ]
            [ IrInt ] Map.empty
          2,
          block [] Map.empty
            [ { Site = (otherValueSites[1] |> fun (_, siteId, _) -> siteId); Operation = IrOperation.Constant(LInt 30L, IrInt) } ]
            [ IrInt ] Map.empty ]
    let effectFunction =
        functionDefinition effectWord 1 [ enumType ] [ IrInt ] (Set.singleton IrEffect.ConsoleWrite) (Set.singleton IrEffect.ConsoleWrite) Map.empty
            (block [ enumType ] Map.empty
                [ { Site = effectMatchSite; Operation = IrOperation.MatchEnum(enumKey, effectBranches) } ]
                [ IrInt ] Map.empty)
    let effectSources =
        [ yield effectMatchSite, effectMatchSource
          yield effectCallSite, effectCallSource
          yield effectValueSite, effectValueSource
          for _, siteId, sourceEntry in otherValueSites do yield siteId, sourceEntry ]
    let effectCoverage = coverage effectWord [ 0 .. 4 ] [ 0, caseNames ]
    let effectProgram =
        { executable with
            FunctionsById = Map.add effectWord effectFunction executable.FunctionsById
            SourceMap = Map.fold (fun found siteId sourceEntry -> Map.add siteId sourceEntry found) executable.SourceMap (Map.ofList effectSources)
            CoverageByWord = Map.add effectWord effectCoverage executable.CoverageByWord }
    let effectCatalog = Map.ofList [ effectId, effectContract ]
    verify effectCatalog effectProgram
    let undercountedEffect = { effectFunction with FunctionInferredEffects = noEffects }
    expectDiagnostic "enum-match arm effects contribute to inferred function effects" "IR_FUNCTION_EFFECT_MISMATCH" (fun () ->
        verify effectCatalog { effectProgram with FunctionsById = Map.add effectWord undercountedEffect effectProgram.FunctionsById })
    let underdeclaredEffect = { effectFunction with FunctionDeclaredEffects = noEffects }
    expectDiagnostic "enum-match arm effects must be declared by the function" "IR_UNDECLARED_EFFECT" (fun () ->
        verify effectCatalog { effectProgram with FunctionsById = Map.add effectWord underdeclaredEffect effectProgram.FunctionsById })

let private verifiedFiniteFixture catalog nominalTypes signatureTypes =
    let owner = WordId "finite-coverage-identity"
    let identity = functionWithCode owner 1 signatureTypes signatureTypes noEffects noEffects Map.empty []
    let candidate = program [ owner, identity ] [] nominalTypes [] [ owner, coverage owner [] [] ]
    let verified = IrVerifier.verify catalog candidate
    let inspected = VerifiedIrProgram.inspect verified
    inspected, inspected.FunctionsById[owner]

let private finitePosition name positions index =
    match positions |> List.tryFind (fun position -> position.Position = index) with
    | Some position -> position
    | None -> failwith $"{name}: missing finite coverage position {index}"

let private expectFiniteLabels name expected actual =
    check name ((Set.ofList expected) = (Set.ofList actual) && List.length expected = List.length actual)

let private testFiniteCoverageClosedDomains () =
    let flagsKey = ProgramTypeKey 0
    let flagsType = IrNominal flagsKey
    let flagsDefinition =
        flagsKey,
        IrRecordDefinition
            { TypeKey = flagsKey
              TypeName = "Flags"
              RecordFields =
                [ { FieldIndex = 0; FieldName = "left"; FieldType = IrBool }
                  { FieldIndex = 1; FieldName = "right"; FieldType = IrBool } ] }
    let boolOption = IrOption IrBool
    let boolUnitResult = IrResult(IrBool, IrUnit)
    let types = [ IrBool; IrUnit; boolOption; boolUnitResult; flagsType ]
    let inspected, owner = verifiedFiniteFixture Map.empty [ flagsDefinition ] types
    let flags left right = RecordValue("Flags", Map.ofList [ "left", BoolValue left; "right", BoolValue right ])
    let option value = OptionValue(TBool, value)
    let result value = ResultValue(TBool, TUnit, value)
    let rows =
        [ [ BoolValue false; UnitValue; option None; result (Ok(BoolValue false)); flags false false ]
          [ BoolValue true; UnitValue; option (Some(BoolValue false)); result (Ok(BoolValue true)); flags false true ]
          [ BoolValue false; UnitValue; option (Some(BoolValue true)); result (Error UnitValue); flags true false ]
          [ BoolValue true; UnitValue; option None; result (Error UnitValue); flags true true ] ]
    let report = FiniteCoverage.analyze inspected owner (List.take 2 rows) rows
    let boolInput = finitePosition "Bool input" report.Inputs 0
    expectFiniteLabels "a Bool input requires both declared values" [ "false"; "true" ] boolInput.Required
    expectFiniteLabels "both Bool input values were observed" [ "false"; "true" ] boolInput.Observed
    check "Bool input has no missing finite values" boolInput.Missing.IsEmpty

    let boolReturn = finitePosition "Bool return" report.Returns 0
    let unitReturn = finitePosition "Unit return" report.Returns 1
    let optionReturn = finitePosition "Option Bool return" report.Returns 2
    let resultReturn = finitePosition "Result Bool Unit return" report.Returns 3
    let flagsReturn = finitePosition "finite record return" report.Returns 4
    expectFiniteLabels "Bool return domain is explicitly false and true" [ "false"; "true" ] boolReturn.Required
    expectFiniteLabels "Unit return domain contains its one value" [ "unit" ] unitReturn.Required
    expectFiniteLabels "Option Bool includes none and each Some payload" [ "none"; "some(false)"; "some(true)" ] optionReturn.Required
    expectFiniteLabels "Result Bool Unit includes both Ok payloads and Error Unit" [ "ok(false)"; "ok(true)"; "error(unit)" ] resultReturn.Required
    expectFiniteLabels "two Bool fields require all four record values"
        [ "Flags{left=false, right=false}"; "Flags{left=false, right=true}"; "Flags{left=true, right=false}"; "Flags{left=true, right=true}" ] flagsReturn.Required
    for position in report.Returns do check $"return position {position.Position} is fully observed" position.Missing.IsEmpty
    check "fully finite closed domains have no unsupported entries" report.Unsupported.IsEmpty

let private testFiniteCoverageIndependentInputsAndNominality () =
    let firstKey = ProgramTypeKey 0
    let secondKey = ProgramTypeKey 1
    let firstType = IrNominal firstKey
    let secondType = IrNominal secondKey
    let enumDefinition key name =
        key,
        IrEnumDefinition
            { TypeKey = key
              TypeName = name
              Cases = [ "ready"; "done" ] }
    let enums = [ enumDefinition firstKey "SignalA"; enumDefinition secondKey "SignalB" ]

    let signature = [ IrBool; IrBool; firstType ]
    let inspected, owner = verifiedFiniteFixture Map.empty enums signature
    let independentRows =
        [ [ BoolValue false; BoolValue false; EnumValue("SignalA", "ready") ]
          [ BoolValue true; BoolValue true; EnumValue("SignalA", "done") ] ]
    let report = FiniteCoverage.analyze inspected owner independentRows []
    for index in 0..1 do
        let parameter = finitePosition "independent Bool parameter" report.Inputs index
        expectFiniteLabels $"Bool parameter {index} requires only its own two values" [ "false"; "true" ] parameter.Required
        expectFiniteLabels $"Bool parameter {index} observes both values without Cartesian input combinations" [ "false"; "true" ] parameter.Observed
        check $"Bool parameter {index} has no missing values despite correlated test rows" parameter.Missing.IsEmpty
    let enumInput = finitePosition "enum input" report.Inputs 2
    expectFiniteLabels "enum parameter requires both declared cases" [ "SignalA.done"; "SignalA.ready" ] enumInput.Required
    check "enum parameter has each declared case" enumInput.Missing.IsEmpty

    let nominalSignature = [ firstType; secondType ]
    let nominalProgram, nominalOwner = verifiedFiniteFixture Map.empty enums nominalSignature
    let wrongNominalRows =
        [ [ EnumValue("SignalA", "ready"); EnumValue("SignalA", "ready") ]
          [ EnumValue("SignalA", "done"); EnumValue("SignalA", "done") ] ]
    let nominalReport = FiniteCoverage.analyze nominalProgram nominalOwner wrongNominalRows []
    let firstParameter = finitePosition "first enum identity" nominalReport.Inputs 0
    let secondParameter = finitePosition "second enum identity" nominalReport.Inputs 1
    expectFiniteLabels "first nominal enum has its own same-named case labels" [ "SignalA.done"; "SignalA.ready" ] firstParameter.Required
    check "first nominal enum receives only its own cases" firstParameter.Missing.IsEmpty
    expectFiniteLabels "second nominal enum retains distinct obligations despite overlapping case names" [ "SignalB.done"; "SignalB.ready" ] secondParameter.Required
    check "same-label cases from a different nominal key cannot credit this enum" (secondParameter.Observed.IsEmpty && secondParameter.Required |> List.forall (fun required -> List.contains required secondParameter.Missing))

let private testFiniteCoverageOpenAndNestedRecords () =
    let flagsKey = ProgramTypeKey 0
    let mixedKey = ProgramTypeKey 1
    let outerKey = ProgramTypeKey 2
    let flagsType = IrNominal flagsKey
    let mixedType = IrNominal mixedKey
    let outerType = IrNominal outerKey
    let flagsDefinition =
        flagsKey,
        IrRecordDefinition
            { TypeKey = flagsKey
              TypeName = "Flags"
              RecordFields =
                [ { FieldIndex = 0; FieldName = "left"; FieldType = IrBool }
                  { FieldIndex = 1; FieldName = "right"; FieldType = IrBool } ] }
    let mixedDefinition =
        mixedKey,
        IrRecordDefinition
            { TypeKey = mixedKey
              TypeName = "Mixed"
              RecordFields =
                [ { FieldIndex = 0; FieldName = "active"; FieldType = IrBool }
                  { FieldIndex = 1; FieldName = "visible"; FieldType = IrBool }
                  { FieldIndex = 2; FieldName = "attempts"; FieldType = IrInt } ] }
    let outerDefinition =
        outerKey,
        IrRecordDefinition
            { TypeKey = outerKey
              TypeName = "Outer"
              RecordFields =
                [ { FieldIndex = 0; FieldName = "flags"; FieldType = flagsType }
                  { FieldIndex = 1; FieldName = "name"; FieldType = IrString } ] }
    let inspected, owner = verifiedFiniteFixture Map.empty [ flagsDefinition; mixedDefinition; outerDefinition ] [ mixedType; outerType ]
    let flags left right = RecordValue("Flags", Map.ofList [ "left", BoolValue left; "right", BoolValue right ])
    let mixed active visible attempts =
        RecordValue("Mixed", Map.ofList [ "active", BoolValue active; "visible", BoolValue visible; "attempts", IntValue attempts ])
    let outer left right name = RecordValue("Outer", Map.ofList [ "flags", flags left right; "name", StringValue name ])
    let rows =
        [ [ mixed false false 1L; outer false false "a" ]
          [ mixed true false 2L; outer false true "b" ]
          [ mixed false true 3L; outer true false "c" ]
          [ mixed true true 4L; outer true true "d" ] ]
    let report = FiniteCoverage.analyze inspected owner [] rows
    let mixedReturn = finitePosition "mixed finite/open record" report.Returns 0
    expectFiniteLabels "each Bool projection is covered independently in a record with an open Int field"
        [ "$.active: false"; "$.active: true"; "$.visible: false"; "$.visible: true" ] mixedReturn.Required
    check "finite projections of mixed record are covered" mixedReturn.Missing.IsEmpty
    let outerReturn = finitePosition "nested finite record in open record" report.Returns 1
    expectFiniteLabels "nested finite record retains all four values while outer String stays open"
        [ "$.flags: Flags{left=false, right=false}"; "$.flags: Flags{left=false, right=true}"; "$.flags: Flags{left=true, right=false}"; "$.flags: Flags{left=true, right=true}" ] outerReturn.Required
    check "each nested finite record value was observed" outerReturn.Missing.IsEmpty
    check "open record fields do not make the whole type unsupported" report.Unsupported.IsEmpty

let private testFiniteCoverageUnsupportedAndBoundedDomains () =
    let validatorId, validatorContract = primitiveContract "finite.bool.valid?" [ PatternBool ] [ PatternBool ] noEffects
    let validator = resolved (PrimitiveTarget validatorId) "finite.bool.valid?" [ IrBool ] [ IrBool ] noEffects
    let refinedKey = ProgramTypeKey 0
    let refinedType = IrNominal refinedKey
    let refinedDefinition =
        refinedKey,
        IrScalarDefinition
            { TypeKey = refinedKey
              TypeName = "RefinedFlag"
              BaseType = IrBool
              ValidatorCall = Some validator }
    let refinedProgram, refinedOwner =
        verifiedFiniteFixture (Map.ofList [ validatorId, validatorContract ]) [ refinedDefinition ] [ refinedType ]
    let refinedReport = FiniteCoverage.analyze refinedProgram refinedOwner [] []
    check "a refined finite-base scalar is explicitly unsupported" (refinedReport.Unsupported |> List.exists (fun issue -> issue.Contains("refined scalar RefinedFlag")))
    check "unsupported refined scalar does not report an enumerable return domain" refinedReport.Returns.IsEmpty

    let textValidatorId, textValidatorContract = primitiveContract "finite.text.valid?" [ PatternString ] [ PatternBool ] noEffects
    let countValidatorId, countValidatorContract = primitiveContract "finite.count.valid?" [ PatternInt ] [ PatternBool ] noEffects
    let textValidator = resolved (PrimitiveTarget textValidatorId) "finite.text.valid?" [ IrString ] [ IrBool ] noEffects
    let countValidator = resolved (PrimitiveTarget countValidatorId) "finite.count.valid?" [ IrInt ] [ IrBool ] noEffects
    let textKey = ProgramTypeKey 0
    let countKey = ProgramTypeKey 1
    let textType = IrNominal textKey
    let countType = IrNominal countKey
    let refinedText =
        textKey,
        IrScalarDefinition
            { TypeKey = textKey
              TypeName = "CustomerId"
              BaseType = IrString
              ValidatorCall = Some textValidator }
    let refinedCount =
        countKey,
        IrScalarDefinition
            { TypeKey = countKey
              TypeName = "OrderCount"
              BaseType = IrInt
              ValidatorCall = Some countValidator }
    let openResultType = IrResult(textType, IrBool)
    let openCatalog = Map.ofList [ textValidatorId, textValidatorContract; countValidatorId, countValidatorContract ]
    let openProgram, openOwner =
        verifiedFiniteFixture openCatalog [ refinedText; refinedCount ] [ textType; countType; openResultType ]
    let resultValue payload = ResultValue(TNamed "CustomerId", TBool, payload)
    let openRows =
        [ [ NamedValue("CustomerId", StringValue "acct-1")
            NamedValue("OrderCount", IntValue 3L)
            resultValue (Ok(NamedValue("CustomerId", StringValue "acct-1"))) ]
          [ NamedValue("CustomerId", StringValue "acct-2")
            NamedValue("OrderCount", IntValue 8L)
            resultValue (Error(BoolValue false)) ]
          [ NamedValue("CustomerId", StringValue "acct-3")
            NamedValue("OrderCount", IntValue 13L)
            resultValue (Error(BoolValue true)) ] ]
    let openReport = FiniteCoverage.analyze openProgram openOwner [] openRows
    let textReturn = finitePosition "refined String return" openReport.Returns 0
    let countReturn = finitePosition "refined Int return" openReport.Returns 1
    expectFiniteLabels "a refined String keeps its payload domain open" [] textReturn.Required
    expectFiniteLabels "a refined Int keeps its payload domain open" [] countReturn.Required
    let resultReturn = finitePosition "Result of refined String and Bool" openReport.Returns 2
    expectFiniteLabels "Result with open refined String still requires both tags and finite Bool error values"
        [ "ok"; "error"; "$.error: false"; "$.error: true" ] resultReturn.Required
    check "Result open payload and both finite error values were observed" resultReturn.Missing.IsEmpty
    check "refined open scalar payloads do not make the composite unsupported" openReport.Unsupported.IsEmpty

    let cycleKey = ProgramTypeKey 0
    let cycleType = IrNominal cycleKey
    let cycleDefinition =
        cycleKey,
        IrRecordDefinition
            { TypeKey = cycleKey
              TypeName = "Cycle"
              RecordFields = [ { FieldIndex = 0; FieldName = "next"; FieldType = IrOption cycleType } ] }
    let cycleProgram, cycleOwner = verifiedFiniteFixture Map.empty [ cycleDefinition ] [ cycleType ]
    let cycleReport = FiniteCoverage.analyze cycleProgram cycleOwner [] []
    check "recursive record expansion terminates with an explicit unsupported result"
        (cycleReport.Unsupported |> List.exists (fun issue -> issue.Contains("recursive nominal type Cycle")))
    check "recursive record does not return a partial finite domain" cycleReport.Returns.IsEmpty

    let record key name fieldCount =
        key,
        IrRecordDefinition
            { TypeKey = key
              TypeName = name
              RecordFields =
                [ for index in 0 .. fieldCount - 1 ->
                    { FieldIndex = index
                      FieldName = $"flag{index}"
                      FieldType = IrBool } ] }
    let atLimitKey = ProgramTypeKey 0
    let overLimitKey = ProgramTypeKey 1
    let atLimitProgram, atLimitOwner =
        verifiedFiniteFixture Map.empty [ record atLimitKey "AtLimit" 12 ] [ IrNominal atLimitKey ]
    let atLimitReport = FiniteCoverage.analyze atLimitProgram atLimitOwner [] []
    let atLimit = finitePosition "4,096-value finite record" atLimitReport.Returns 0
    check "the documented 4,096-value limit is accepted exactly" (atLimit.Required.Length = 4096 && atLimitReport.Unsupported.IsEmpty)

    let overLimitProgram, overLimitOwner =
        verifiedFiniteFixture Map.empty [ record overLimitKey "OverLimit" 13 ] [ IrNominal overLimitKey ]
    let overLimitReport = FiniteCoverage.analyze overLimitProgram overLimitOwner [] []
    check "a finite product above 4,096 values is explicitly unsupported"
        (overLimitReport.Unsupported |> List.exists (fun issue -> issue.Contains("OverLimit") && issue.Contains("4096")))
    check "oversized finite products do not expose a partial domain" overLimitReport.Returns.IsEmpty

let private testFiniteCoverageRejectsMalformedGenericMetadata () =
    let types = [ IrOption IrBool; IrResult(IrBool, IrUnit) ]
    let inspected, owner = verifiedFiniteFixture Map.empty [] types
    let malformedOption = OptionValue(TString, Some(BoolValue true))
    let malformedResult = ResultValue(TString, TUnit, Ok(BoolValue true))
    let report = FiniteCoverage.analyze inspected owner [] [ [ malformedOption; malformedResult ] ]
    let optionReturn = finitePosition "Option generic metadata" report.Returns 0
    let resultReturn = finitePosition "Result generic metadata" report.Returns 1
    expectFiniteLabels "Option expected domain remains none and both Some values" [ "none"; "some(false)"; "some(true)" ] optionReturn.Required
    expectFiniteLabels "Result expected domain remains both Ok values and Error Unit" [ "ok(false)"; "ok(true)"; "error(unit)" ] resultReturn.Required
    check "malformed Option type metadata contributes no observed finite value"
        (optionReturn.Observed.IsEmpty && optionReturn.Required |> List.forall (fun required -> List.contains required optionReturn.Missing))
    check "malformed Result type metadata contributes no observed finite value"
        (resultReturn.Observed.IsEmpty && resultReturn.Required |> List.forall (fun required -> List.contains required resultReturn.Missing))

let private testGeneratedRecordAndScalarOperations () =
    let customerKey = ProgramTypeKey 0
    let emailKey = ProgramTypeKey 1
    let customerType = IrNominal customerKey
    let emailType = IrNominal emailKey
    let customer =
        IrRecordDefinition
            { TypeKey = customerKey
              TypeName = "Customer"
              RecordFields =
                [ { FieldIndex = 0; FieldName = "id"; FieldType = IrInt }
                  { FieldIndex = 1; FieldName = "email"; FieldType = emailType } ] }
    let emailValidatorId, emailValidatorContract =
        primitiveContract "email.valid?" [ PatternString ] [ PatternBool ] noEffects
    let validator = resolved (PrimitiveTarget emailValidatorId) "email.valid?" [ IrString ] [ IrBool ] noEffects
    let email =
        IrScalarDefinition
            { TypeKey = emailKey
              TypeName = "Email"
              BaseType = IrString
              ValidatorCall = Some validator }

    let constructorId = WordId "customer.new"
    let accessorId = WordId "customer.id"
    let emailNewId = WordId "email.new"
    let emailValueId = WordId "email.value"
    let constructorSite, constructorSource = source constructorId 0 "record-declaration"
    let accessorSite, accessorSource = source accessorId 0 "field-declaration"
    let emailNewSite, emailNewSource = source emailNewId 0 "scalar-declaration"
    let emailValueSite, emailValueSource = source emailValueId 0 "scalar-declaration"
    let generatedTarget id name operation inputs outputs mappedSite =
        { TargetId = id
          TargetRevision = 1
          TargetName = name
          Operation = operation
          InputTypes = inputs
          OutputTypes = outputs
          TargetDeclaredEffects = noEffects
          TargetEffects = noEffects
          SourceSite = Some mappedSite }
    let targets =
        [ constructorId, generatedTarget constructorId "Customer.new" (MakeRecordOperation customerKey) [ IrInt; emailType ] [ customerType ] constructorSite
          accessorId, generatedTarget accessorId "Customer.id" (GetRecordFieldOperation(customerKey, 0)) [ customerType ] [ IrInt ] accessorSite
          emailNewId, generatedTarget emailNewId "Email.new" (WrapScalarOperation emailKey) [ IrString ] [ emailType ] emailNewSite
          emailValueId, generatedTarget emailValueId "Email.value" (UnwrapScalarOperation emailKey) [ emailType ] [ IrString ] emailValueSite ]

    let makeCustomerWord = WordId "make-customer"
    let getCustomerIdWord = WordId "get-customer-id"
    let wrapEmailWord = WordId "wrap-email"
    let unwrapEmailWord = WordId "unwrap-email"
    let makeSite, makeSource = source makeCustomerWord 0 "record-constructor"
    let getSite, getSource = source getCustomerIdWord 0 "record-accessor"
    let wrapSite, wrapSource = source wrapEmailWord 0 "scalar-constructor"
    let unwrapSite, unwrapSource = source unwrapEmailWord 0 "scalar-accessor"
    let makeCall = resolved (GeneratedWordTarget(constructorId, 1)) "Customer.new" [ IrInt; emailType ] [ customerType ] noEffects
    let getCall = resolved (GeneratedWordTarget(accessorId, 1)) "Customer.id" [ customerType ] [ IrInt ] noEffects
    let wrapCall = resolved (GeneratedWordTarget(emailNewId, 1)) "Email.new" [ IrString ] [ emailType ] noEffects
    let unwrapCall = resolved (GeneratedWordTarget(emailValueId, 1)) "Email.value" [ emailType ] [ IrString ] noEffects
    let functions =
        [ makeCustomerWord,
          functionWithCode makeCustomerWord 1 [ IrInt; emailType ] [ customerType ] noEffects noEffects Map.empty
            [ { Site = makeSite; Operation = IrOperation.MakeRecord(makeCall, customerKey) } ]
          getCustomerIdWord,
          functionWithCode getCustomerIdWord 1 [ customerType ] [ IrInt ] noEffects noEffects Map.empty
            [ { Site = getSite; Operation = IrOperation.GetRecordField(getCall, customerKey, 0) } ]
          wrapEmailWord,
          functionWithCode wrapEmailWord 1 [ IrString ] [ emailType ] noEffects noEffects Map.empty
            [ { Site = wrapSite; Operation = IrOperation.WrapScalar(wrapCall, emailKey, Some validator) } ]
          unwrapEmailWord,
          functionWithCode unwrapEmailWord 1 [ emailType ] [ IrString ] noEffects noEffects Map.empty
            [ { Site = unwrapSite; Operation = IrOperation.UnwrapScalar(unwrapCall, emailKey) } ] ]
    let executable =
        program functions targets [ customerKey, customer; emailKey, email ]
            [ constructorSite, constructorSource; accessorSite, accessorSource; emailNewSite, emailNewSource; emailValueSite, emailValueSource
              makeSite, makeSource; getSite, getSource; wrapSite, wrapSource; unwrapSite, unwrapSource ]
            [ makeCustomerWord, coverage makeCustomerWord [ 0 ] []
              getCustomerIdWord, coverage getCustomerIdWord [ 0 ] []
              wrapEmailWord, coverage wrapEmailWord [ 0 ] []
              unwrapEmailWord, coverage unwrapEmailWord [ 0 ] [] ]
    verify (Map.ofList [ emailValidatorId, emailValidatorContract ]) executable
    check "record constructor/accessor and refined scalar wrap/unwrap verify against stable generated targets" true

    let wrapDefinition = functions |> List.find (fun (id, _) -> id = wrapEmailWord) |> snd
    let wrongOperation =
        { wrapDefinition with
            FunctionBody = block [ IrString ] Map.empty
                [ { Site = wrapSite; Operation = IrOperation.UnwrapScalar(wrapCall, emailKey) } ] [ emailType ] Map.empty }
    let wrongProgram = { executable with FunctionsById = Map.add wrapEmailWord wrongOperation executable.FunctionsById }
    expectDiagnostic "generated operation must match its exact target kind" "IR_GENERATED_OPERATION_MISMATCH" (fun () -> verify (Map.ofList [ emailValidatorId, emailValidatorContract ]) wrongProgram)

    let cyclicType = IrScalarDefinition { TypeKey = emailKey; TypeName = "Loop"; BaseType = IrOption emailType; ValidatorCall = None }
    let cyclicProgram =
        program [] [] [ emailKey, cyclicType ] [] []
    expectDiagnostic "scalar refinement cannot recursively contain itself" "IR_SCALAR_CYCLE" (fun () -> verify Map.empty cyclicProgram)
    let containerScalar = IrScalarDefinition { TypeKey = ProgramTypeKey 2; TypeName = "Emails"; BaseType = IrList IrString; ValidatorCall = None }
    let containerScalarProgram = program [] [] [ ProgramTypeKey 2, containerScalar ] [] []
    expectDiagnostic "scalar wrappers cannot use container bases" "IR_SCALAR_BASE_UNSUPPORTED" (fun () -> verify Map.empty containerScalarProgram)

let private testStaticCallbacksAndEffects () =
    let toTextPrimitive, toTextContract = primitiveContract "int.to-string" [ PatternInt ] [ PatternString ] noEffects
    let positivePrimitive, positiveContract = primitiveContract "int.positive?" [ PatternInt ] [ PatternBool ] noEffects
    let consolePrimitive, consoleContract = primitiveContract "console.write" [ PatternString ] [ PatternUnit ] (Set.singleton IrEffect.ConsoleWrite)
    let catalog = Map.ofList [ toTextPrimitive, toTextContract; positivePrimitive, positiveContract; consolePrimitive, consoleContract ]
    let toTextWord = WordId "int-text"
    let positiveWord = WordId "int-positive"
    let printWord = WordId "print-int"
    let mapWord = WordId "map-int-text"
    let filterWord = WordId "filter-positive"
    let eachWord = WordId "each-print"

    let toTextSite, toTextSource = source toTextWord 0 "call"
    let positiveSite, positiveSource = source positiveWord 0 "call"
    let printTextSite, printTextSource = source printWord 0 "call"
    let printConsoleSite, printConsoleSource = source printWord 1 "call"
    let mapSite, mapSource = source mapWord 0 "list-map"
    let filterSite, filterSource = source filterWord 0 "list-filter"
    let eachSite, eachSource = source eachWord 0 "list-each"
    let toTextCall = resolved (PrimitiveTarget toTextPrimitive) "int.to-string" [ IrInt ] [ IrString ] noEffects
    let positiveCall = resolved (PrimitiveTarget positivePrimitive) "int.positive?" [ IrInt ] [ IrBool ] noEffects
    let printTextCall = resolved (PrimitiveTarget toTextPrimitive) "int.to-string" [ IrInt ] [ IrString ] noEffects
    let printConsoleCall = resolved (PrimitiveTarget consolePrimitive) "console.write" [ IrString ] [ IrUnit ] (Set.singleton IrEffect.ConsoleWrite)
    let toTextFunction = functionWithCode toTextWord 1 [ IrInt ] [ IrString ] noEffects noEffects Map.empty [ { Site = toTextSite; Operation = IrOperation.Call toTextCall } ]
    let positiveFunction = functionWithCode positiveWord 1 [ IrInt ] [ IrBool ] noEffects noEffects Map.empty [ { Site = positiveSite; Operation = IrOperation.Call positiveCall } ]
    let printFunction =
        functionWithCode printWord 1 [ IrInt ] [ IrUnit ] (Set.singleton IrEffect.ConsoleWrite) (Set.singleton IrEffect.ConsoleWrite) Map.empty
            [ { Site = printTextSite; Operation = IrOperation.Call printTextCall }
              { Site = printConsoleSite; Operation = IrOperation.Call printConsoleCall } ]

    let toTextCallback = resolved (UserWordTarget(toTextWord, 1)) "int-text" [ IrInt ] [ IrString ] noEffects
    let positiveCallback = resolved (UserWordTarget(positiveWord, 1)) "int-positive" [ IrInt ] [ IrBool ] noEffects
    let printCallback = resolved (UserWordTarget(printWord, 1)) "print-int" [ IrInt ] [ IrUnit ] (Set.singleton IrEffect.ConsoleWrite)
    let mapFunction = functionWithCode mapWord 1 [ IrList IrInt ] [ IrList IrString ] noEffects noEffects Map.empty [ { Site = mapSite; Operation = IrOperation.ListMap(toTextCallback, IrInt, IrString) } ]
    let filterFunction = functionWithCode filterWord 1 [ IrList IrInt ] [ IrList IrInt ] noEffects noEffects Map.empty [ { Site = filterSite; Operation = IrOperation.ListFilter(positiveCallback, IrInt) } ]
    let eachFunction = functionWithCode eachWord 1 [ IrList IrInt ] [ IrUnit ] (Set.singleton IrEffect.ConsoleWrite) (Set.singleton IrEffect.ConsoleWrite) Map.empty [ { Site = eachSite; Operation = IrOperation.ListEach(printCallback, IrInt) } ]

    let functions =
        [ toTextWord, toTextFunction; positiveWord, positiveFunction; printWord, printFunction
          mapWord, mapFunction; filterWord, filterFunction; eachWord, eachFunction ]
    let sources =
        [ toTextSite, toTextSource; positiveSite, positiveSource; printTextSite, printTextSource; printConsoleSite, printConsoleSource
          mapSite, mapSource; filterSite, filterSource; eachSite, eachSource ]
    let coverageByWord =
        [ toTextWord, coverage toTextWord [ 0 ] []
          positiveWord, coverage positiveWord [ 0 ] []
          printWord, coverage printWord [ 0; 1 ] []
          mapWord, coverage mapWord [ 0 ] [ 0, [ "empty"; "nonempty" ] ]
          filterWord, coverage filterWord [ 0 ] [ 0, [ "empty"; "nonempty"; "keep"; "drop" ] ]
          eachWord, coverage eachWord [ 0 ] [ 0, [ "empty"; "nonempty" ] ] ]
    let executable = program functions [] [] sources coverageByWord
    verify catalog executable
    check "map/filter/each validate static callback signatures and exact coverage categories" true
    check "each inherits callback effects even for an empty runtime list" (eachFunction.FunctionInferredEffects.Contains IrEffect.ConsoleWrite)

    let missingEffect = { eachFunction with FunctionInferredEffects = noEffects }
    let undercounted = { executable with FunctionsById = Map.add eachWord missingEffect executable.FunctionsById }
    expectDiagnostic "empty-list callback effects remain a compile-time obligation" "IR_FUNCTION_EFFECT_MISMATCH" (fun () -> verify catalog undercounted)

    let wrongMap =
        { mapFunction with
            InputTypes = [ IrList IrString ]
            FunctionBody = block [ IrList IrString ] Map.empty
                [ { Site = mapSite; Operation = IrOperation.ListMap(toTextCallback, IrString, IrString) } ]
                [ IrList IrString ] Map.empty }
    let wrongCallback = { executable with FunctionsById = Map.add mapWord wrongMap executable.FunctionsById }
    expectDiagnostic "map rejects a callback whose input differs from its element type" "IR_CALLBACK_SIGNATURE_MISMATCH" (fun () -> verify catalog wrongCallback)

let private testListFoldVerification () =
    let accumulatorKey = ProgramTypeKey 21
    let itemKey = ProgramTypeKey 22
    let accumulatorType = IrNominal accumulatorKey
    let itemType = IrNominal itemKey
    let accumulatorDefinition =
        accumulatorKey,
        IrScalarDefinition
            { TypeKey = accumulatorKey
              TypeName = "Accumulator"
              BaseType = IrInt
              ValidatorCall = None }
    let itemDefinition =
        itemKey,
        IrRecordDefinition
            { TypeKey = itemKey
              TypeName = "Item"
              RecordFields = [ { FieldIndex = 0; FieldName = "marker"; FieldType = IrInt } ] }
    let stepId, stepContract = primitiveContract "fold.step" [ PatternVariable 0; PatternVariable 1 ] [ PatternVariable 0 ] noEffects
    let tripleId, tripleContract = primitiveContract "fold.triple" [ PatternVariable 0; PatternVariable 1; PatternVariable 2 ] [ PatternVariable 0 ] noEffects
    let boolResultId, boolResultContract = primitiveContract "fold.bool-result" [ PatternVariable 0; PatternVariable 1 ] [ PatternBool ] noEffects
    let effectId, effectContract = primitiveContract "fold.effect" [ PatternVariable 0; PatternVariable 1 ] [ PatternVariable 0 ] (Set.singleton IrEffect.ConsoleWrite)
    let catalog = Map.ofList [ stepId, stepContract; tripleId, tripleContract; boolResultId, boolResultContract; effectId, effectContract ]
    let callback target name inputs outputs effects = resolved (PrimitiveTarget target) name inputs outputs effects
    let foldWord = WordId "fold-nominal-owner"
    let foldSite, foldSource = source foldWord 0 "list-fold"
    let step = callback stepId "fold.step" [ accumulatorType; itemType ] [ accumulatorType ] noEffects
    let ownerFunction =
        functionWithCode foldWord 1 [ IrBool; IrList itemType; accumulatorType ] [ IrBool; accumulatorType ] noEffects noEffects Map.empty
            [ { Site = foldSite; Operation = IrOperation.ListFold(step, itemType, accumulatorType) } ]
    let executable =
        program [ foldWord, ownerFunction ] [] [ accumulatorDefinition; itemDefinition ]
            [ foldSite, foldSource ] [ foldWord, coverage foldWord [ 0 ] [ 0, [ "empty"; "nonempty" ] ] ]
    verify catalog executable
    check "ListFold verifies distinct nominal item and accumulator types and preserves its stack prefix" true

    let replaceFold operation functionEffects =
        let updatedFunction =
            { ownerFunction with
                FunctionDeclaredEffects = functionEffects
                FunctionInferredEffects = functionEffects
                FunctionBody = block [ IrBool; IrList itemType; accumulatorType ] Map.empty
                    [ { Site = foldSite; Operation = operation } ] [ IrBool; accumulatorType ] Map.empty }
        { executable with FunctionsById = Map.add foldWord updatedFunction executable.FunctionsById }
    let reversed = callback stepId "fold.step" [ itemType; accumulatorType ] [ itemType ] noEffects
    expectDiagnostic "fold rejects callback arguments in the wrong order" "IR_CALLBACK_SIGNATURE_MISMATCH"
        (fun () -> verify catalog (replaceFold (IrOperation.ListFold(reversed, itemType, accumulatorType)) noEffects))

    let wrongArity = callback tripleId "fold.triple" [ accumulatorType; itemType; IrBool ] [ accumulatorType ] noEffects
    expectDiagnostic "fold rejects a callback with the wrong arity" "IR_CALLBACK_SIGNATURE_MISMATCH"
        (fun () -> verify catalog (replaceFold (IrOperation.ListFold(wrongArity, itemType, accumulatorType)) noEffects))

    let wrongOutput = callback boolResultId "fold.bool-result" [ accumulatorType; itemType ] [ IrBool ] noEffects
    expectDiagnostic "fold rejects a callback whose output differs from its accumulator" "IR_CALLBACK_SIGNATURE_MISMATCH"
        (fun () -> verify catalog (replaceFold (IrOperation.ListFold(wrongOutput, itemType, accumulatorType)) noEffects))

    let wrongItem = callback stepId "fold.step" [ accumulatorType; IrBool ] [ accumulatorType ] noEffects
    expectDiagnostic "fold rejects a callback input that disagrees with its closed list element type" "IR_CALLBACK_SIGNATURE_MISMATCH"
        (fun () -> verify catalog (replaceFold (IrOperation.ListFold(wrongItem, itemType, accumulatorType)) noEffects))
    expectDiagnostic "fold rejects a forged item annotation that disagrees with the actual list element type" "IR_LIST_STACK_MISMATCH"
        (fun () -> verify catalog (replaceFold (IrOperation.ListFold(wrongItem, IrBool, accumulatorType)) noEffects))

    let effectful = callback effectId "fold.effect" [ accumulatorType; itemType ] [ accumulatorType ] (Set.singleton IrEffect.ConsoleWrite)
    let effectWord = WordId "fold-effect-owner"
    let effectSite, effectSource = source effectWord 0 "list-fold"
    let effectFunction =
        functionWithCode effectWord 1 [ IrList itemType; accumulatorType ] [ accumulatorType ]
            (Set.singleton IrEffect.ConsoleWrite) (Set.singleton IrEffect.ConsoleWrite) Map.empty
            [ { Site = effectSite; Operation = IrOperation.ListFold(effectful, itemType, accumulatorType) } ]
    let effectProgram =
        program [ effectWord, effectFunction ] [] [ accumulatorDefinition; itemDefinition ]
            [ effectSite, effectSource ] [ effectWord, coverage effectWord [ 0 ] [ 0, [ "empty"; "nonempty" ] ] ]
    verify catalog effectProgram
    let undercounted = { effectFunction with FunctionInferredEffects = noEffects }
    let undercountedProgram = { effectProgram with FunctionsById = Map.add effectWord undercounted effectProgram.FunctionsById }
    expectDiagnostic "fold callback effects remain mandatory when the list may be empty" "IR_FUNCTION_EFFECT_MISMATCH"
        (fun () -> verify catalog undercountedProgram)
    let underdeclared = { effectFunction with FunctionDeclaredEffects = noEffects }
    let underdeclaredProgram = { effectProgram with FunctionsById = Map.add effectWord underdeclared effectProgram.FunctionsById }
    expectDiagnostic "fold rejects a forged declaration that omits callback effects" "IR_UNDECLARED_EFFECT"
        (fun () -> verify catalog underdeclaredProgram)

let private testIdentityAndCallGraphGuards () =
    let left = WordId "cycle-left"
    let right = WordId "cycle-right"
    let leftSite, leftSource = source left 0 "call"
    let rightSite, rightSource = source right 0 "call"
    let leftCall = resolved (UserWordTarget(right, 0)) "cycle-right" [] [] noEffects
    let rightCall = resolved (UserWordTarget(left, 0)) "cycle-left" [] [] noEffects
    let leftFunction = functionWithCode left 0 [] [] noEffects noEffects Map.empty [ { Site = leftSite; Operation = IrOperation.Call leftCall } ]
    let rightFunction = functionWithCode right 0 [] [] noEffects noEffects Map.empty [ { Site = rightSite; Operation = IrOperation.Call rightCall } ]
    let recursive =
        program [ left, leftFunction; right, rightFunction ] [] [] [ leftSite, leftSource; rightSite, rightSource ]
            [ left, coverage left [ 0 ] []; right, coverage right [ 0 ] [] ]
    expectDiagnostic "raw IR call graph cannot bypass recursion checks" "IR_RECURSIVE_CALL_GRAPH" (fun () -> verify Map.empty recursive)

    let foldOwner = WordId "fold-cycle-owner"
    let foldStep = WordId "fold-cycle-step"
    let ownerSite, ownerSource = source foldOwner 0 "list-fold"
    let dropSite, dropSource = source foldStep 0 "call"
    let emptySite, emptySource = source foldStep 1 "list-empty"
    let swapSite, swapSource = source foldStep 2 "call"
    let backEdgeSite, backEdgeSource = source foldStep 3 "call"
    let dropId, dropContract = primitiveContract "drop" [ PatternVariable 0 ] [] noEffects
    let swapId, swapContract = primitiveContract "swap" [ PatternVariable 0; PatternVariable 1 ] [ PatternVariable 1; PatternVariable 0 ] noEffects
    let foldCatalog = Map.ofList [ dropId, dropContract; swapId, swapContract ]
    let dropCall = resolved (PrimitiveTarget dropId) "drop" [ IrInt ] [] noEffects
    let swapCall = resolved (PrimitiveTarget swapId) "swap" [ IrInt; IrList IrInt ] [ IrList IrInt; IrInt ] noEffects
    let backEdge = resolved (UserWordTarget(foldOwner, 1)) "fold-cycle-owner" [ IrList IrInt; IrInt ] [ IrInt ] noEffects
    let foldCallback = resolved (UserWordTarget(foldStep, 1)) "fold-cycle-step" [ IrInt; IrInt ] [ IrInt ] noEffects
    let ownerFunction =
        functionWithCode foldOwner 1 [ IrList IrInt; IrInt ] [ IrInt ] noEffects noEffects Map.empty
            [ { Site = ownerSite; Operation = IrOperation.ListFold(foldCallback, IrInt, IrInt) } ]
    let stepFunction =
        functionWithCode foldStep 1 [ IrInt; IrInt ] [ IrInt ] noEffects noEffects Map.empty
            [ { Site = dropSite; Operation = IrOperation.Call dropCall }
              { Site = emptySite; Operation = IrOperation.ListEmpty IrInt }
              { Site = swapSite; Operation = IrOperation.Call swapCall }
              { Site = backEdgeSite; Operation = IrOperation.Call backEdge } ]
    let foldCycle =
        program [ foldOwner, ownerFunction; foldStep, stepFunction ] [] []
            [ ownerSite, ownerSource; dropSite, dropSource; emptySite, emptySource; swapSite, swapSource; backEdgeSite, backEdgeSource ]
            [ foldOwner, coverage foldOwner [ 0 ] [ 0, [ "empty"; "nonempty" ] ]
              foldStep, coverage foldStep [ 0; 1; 2; 3 ] [] ]
    expectDiagnostic "static fold callbacks participate in IR recursion detection" "IR_RECURSIVE_CALL_GRAPH" (fun () -> verify foldCatalog foldCycle)

    let unknownWord = WordId "unknown-call"
    let unknownSite, unknownSource = source unknownWord 0 "call"
    let callUnknown = resolved (UserWordTarget(WordId "missing", 0)) "missing" [] [] noEffects
    let unknownFunction = functionWithCode unknownWord 0 [] [] noEffects noEffects Map.empty [ { Site = unknownSite; Operation = IrOperation.Call callUnknown } ]
    let unknownProgram = program [ unknownWord, unknownFunction ] [] [] [ unknownSite, unknownSource ] [ unknownWord, coverage unknownWord [ 0 ] [] ]
    expectDiagnostic "unresolved stable word IDs fail before executable verification" "IR_UNKNOWN_WORD_ID" (fun () -> verify Map.empty unknownProgram)

    let sharedId = WordId "same-object-id"
    let sharedSite, sharedSource = source sharedId 0 "empty"
    let sharedFunction = functionWithCode sharedId 0 [] [] noEffects noEffects Map.empty []
    let collidingTarget =
        { TargetId = sharedId
          TargetRevision = 0
          TargetName = "generated.same"
          Operation = MakeRecordOperation(ProgramTypeKey 0)
          InputTypes = []
          OutputTypes = []
          TargetDeclaredEffects = noEffects
          TargetEffects = noEffects
          SourceSite = None }
    let collision =
        program [ sharedId, sharedFunction ] [ sharedId, collidingTarget ] [] [ sharedSite, sharedSource ]
            [ sharedId, coverage sharedId [] [] ]
    expectDiagnostic "user and generated dictionary identities cannot overlap" "IR_WORD_ID_COLLISION" (fun () -> verify Map.empty collision)

    let unknownKey = ProgramTypeKey 6
    let selfType = IrScalarDefinition { TypeKey = unknownKey; TypeName = "Loop"; BaseType = IrOption(IrNominal unknownKey); ValidatorCall = None }
    let cyclicTypeProgram = program [] [] [ unknownKey, selfType ] [] []
    expectDiagnostic "scalar declaration graph cannot contain a self cycle" "IR_SCALAR_CYCLE" (fun () -> verify Map.empty cyclicTypeProgram)

let private testFlatVerifierStackSafety () =
    let instructionCount = 10002
    let owner = WordId "flat-verifier"
    let notId, notContract = primitiveContract "bool.not" [ PatternBool ] [ PatternBool ] noEffects
    let catalog = Map.ofList [ notId, notContract ]
    let notCall = resolved (PrimitiveTarget notId) "bool.not" [ IrBool ] [ IrBool ] noEffects
    let code =
        instruction owner 0 (IrOperation.Constant(LBool true, IrBool))
        :: [ for ordinal in 1 .. instructionCount - 1 -> instruction owner ordinal (IrOperation.Call notCall) ]
    let validFunction =
        functionWithCode owner 1 [] [ IrBool ] noEffects noEffects Map.empty code
    let executable =
        program [ owner, validFunction ] [] []
            [ for ordinal in 0 .. instructionCount - 1 -> source owner ordinal "flat-operation" ]
            [ owner, coverage owner [ 0 .. instructionCount - 1 ] [] ]

    let verified = IrVerifier.verify catalog executable
    check "large flat block verifies without consuming the host call stack"
        ((VerifiedIrProgram.inspect verified).FunctionsById[owner].FunctionBody.Code.Length = instructionCount)

    let malformedCode =
        List.take (instructionCount - 1) code
        @ [ instruction owner (instructionCount - 1) (IrOperation.ListSingleton IrInt) ]
    let malformedFunction =
        { validFunction with
            FunctionBody = block [] Map.empty malformedCode [ IrBool ] Map.empty }
    let malformed =
        { executable with
            FunctionsById = Map.add owner malformedFunction executable.FunctionsById }
    expectDiagnostic "late malformed instruction retains its specific verifier diagnostic" "IR_CONTAINER_PAYLOAD" (fun () -> verify catalog malformed)

let private testCompilerLowering () =
    let line number = testSpan "lowering.agent" number
    let record =
        { Name = "Customer"
          Fields = [ { Name = "id"; Type = TInt }; { Name = "active"; Type = TBool } ]
          SourceText = "record Customer"
          Span = line 1 }
    let scalar =
        { Name = "Email"
          BaseType = TString
          Validator = None
          SourceText = "type Email = String"
          Span = line 2 }
    let generated =
        [ "customer.new", generatedEntry "customer.new" (RecordConstructor "Customer") [ TInt; TBool ] [ TNamed "Customer" ]
          "customer.id", generatedEntry "customer.id" (RecordAccessor("Customer", "id")) [ TNamed "Customer" ] [ TInt ]
          "customer.active", generatedEntry "customer.active" (RecordAccessor("Customer", "active")) [ TNamed "Customer" ] [ TBool ]
          "Email.new", generatedEntry "Email.new" (ScalarConstructor "Email") [ TString ] [ TNamed "Email" ]
          "Email.value", generatedEntry "Email.value" (ScalarAccessor "Email") [ TNamed "Email" ] [ TString ] ]
        |> Map.ofList
    let pureEffects = Set.empty<string>
    let console = Set.singleton "console.write"
    let userWords =
        [ "customer.active?", wordEntry "customer.active?" [ TNamed "Customer" ] [ TBool ] pureEffects
              [ Call("customer.active", line 10)
                If([ Push(LBool true, line 11) ], [ Push(LBool false, line 12) ], line 13) ] 1 Candidate
          "int-positive?", wordEntry "int-positive?" [ TInt ] [ TBool ] pureEffects
              [ Push(LInt 0L, line 20); Call("int.greater-than", line 21) ] 1 Candidate
          "int-text", wordEntry "int-text" [ TInt ] [ TString ] pureEffects [ Call("int.to-string", line 30) ] 1 Candidate
          "int-print", wordEntry "int-print" [ TInt ] [ TUnit ] console
              [ Call("int.to-string", line 40); Call("console.write", line 41) ] 1 Candidate
          "list-to-text", wordEntry "list-to-text" [ TList TInt ] [ TList TString ] pureEffects [ MapList("int.to-string", line 50) ] 1 Candidate
          "list-positive", wordEntry "list-positive" [ TList TInt ] [ TList TInt ] pureEffects [ FilterList("int-positive?", line 60) ] 1 Candidate
          "list-print", wordEntry "list-print" [ TList TInt ] [ TUnit ] console [ EachList("int-print", line 70) ] 1 Candidate
          "customer.fold-email-step", wordEntry "customer.fold-email-step" [ TNamed "Email"; TNamed "Customer" ] [ TNamed "Email" ] pureEffects [ Call("drop", line 71) ] 1 Candidate
          "customers.fold-email", wordEntry "customers.fold-email" [ TList(TNamed "Customer"); TNamed "Email" ] [ TNamed "Email" ] pureEffects [ FoldList("customer.fold-email-step", line 72) ] 1 Candidate
          "local-join", wordEntry "local-join" [ TInt ] [ TInt ] pureEffects
              [ Let("amount", line 80)
                Push(LBool true, line 81)
                If([ Load("amount", line 82); Push(LInt 1L, line 83); Call("add", line 84); Let("amount", line 85) ],
                   [ Load("amount", line 86); Let("amount", line 87) ], line 88)
                Load("amount", line 89) ] 1 Candidate
          "option-value", wordEntry "option-value" [ TOption TInt ] [ TInt ] pureEffects
              [ MatchOption("some-value", [ Load("some-value", line 91) ], [ Push(LInt 0L, line 92) ], line 93) ] 1 Candidate
          "result-value", wordEntry "result-value" [ TResult(TInt, TString) ] [ TInt ] pureEffects
              [ MatchResult("ok-value", "error-value", [ Load("ok-value", line 94) ], [ Push(LInt -1L, line 95) ], line 96) ] 1 Candidate
          "container-constructors", wordEntry "container-constructors" []
              [ TList TInt; TList TInt; TOption TInt; TOption TInt; TResult(TString, TInt); TResult(TString, TInt) ] pureEffects
              [ ConstructContainer(ListEmpty, [ TInt ], line 100)
                Push(LInt 1L, line 101); ConstructContainer(ListSingleton, [ TInt ], line 102)
                ConstructContainer(OptionNone, [ TInt ], line 103)
                Push(LInt 2L, line 104); ConstructContainer(OptionSome, [ TInt ], line 105)
                Push(LString "ok", line 106); ConstructContainer(ResultOk, [ TString; TInt ], line 107)
                Push(LInt 3L, line 108); ConstructContainer(ResultError, [ TString; TInt ], line 109) ] 1 Candidate
          "email-wrap", wordEntry "email-wrap" [ TString ] [ TNamed "Email" ] pureEffects [ Call("Email.new", line 110) ] 1 Candidate
          "email-unwrap", wordEntry "email-unwrap" [ TNamed "Email" ] [ TString ] pureEffects [ Call("Email.value", line 111) ] 1 Candidate
          "customer-build", wordEntry "customer-build" [ TInt; TBool ] [ TNamed "Customer" ] pureEffects [ Call("customer.new", line 112) ] 1 Candidate
          "customer-id", wordEntry "customer-id" [ TNamed "Customer" ] [ TInt ] pureEffects [ Call("customer.id", line 113) ] 1 Candidate
          "branch-read", wordEntry "branch-read" [ TBool ] [ TString ] (Set.singleton "fs.read")
              [ If([ Push(LString "left", line 114); Call("file.read", line 115) ],
                   [ Push(LString "right", line 116); Call("file.read", line 117) ], line 118) ] 1 Candidate
          "equal-int", wordEntry "equal-int" [ TInt; TInt ] [ TBool ] pureEffects [ Call("equals", line 119) ] 1 Candidate
          "equal-string", wordEntry "equal-string" [ TString; TString ] [ TBool ] pureEffects [ Call("equals", line 120) ] 1 Candidate ]
        |> Map.ofList
    let allGeneratedAndUserWords = Map.fold (fun found name entry -> Map.add name entry found) generated userWords
    let context = loweringContext allGeneratedAndUserWords (Map.ofList [ "Customer", record ]) (Map.ofList [ "Email", scalar ])
    let verified = Compiler.compileIrProgram context
    VerifiedIrProgram.requireBackendRegistry Compiler.primitiveIrCatalog verified
    check "compiler lowering returns a backend-authorized snapshot" (VerifiedIrProgram.isBackendExecutable verified)
    let executable = VerifiedIrProgram.inspect verified
    check "nominal table contains sorted Customer and Email definitions" (executable.NominalTypesByKey.Count = 2)
    let wordId name = context.WordIds[name]
    let functionByName name = executable.FunctionsById[wordId name]
    let instructions (block: IrBlock) =
        let rec collect code =
            code
            |> List.collect (fun instruction ->
                let nested =
                    match instruction.Operation with
                    | IrOperation.If(left, right) -> collect left.Code @ collect right.Code
                    | IrOperation.MatchOption(_, someBlock, noneBlock) -> collect someBlock.Code @ collect noneBlock.Code
                    | IrOperation.MatchResult(_, _, okBlock, errorBlock) -> collect okBlock.Code @ collect errorBlock.Code
                    | _ -> []
                instruction :: nested)
        collect block.Code
    let operations name = functionByName name |> fun fn -> instructions fn.FunctionBody |> List.map (fun instruction -> instruction.Operation)
    let calls name =
        operations name
        |> List.choose (function | IrOperation.Call call -> Some call | _ -> None)
    let intEquals = calls "equal-int" |> List.exactlyOne
    let stringEquals = calls "equal-string" |> List.exactlyOne
    check "polymorphic equals is instantiated as Int at one callsite" (intEquals.ResolvedTarget = PrimitiveTarget(PrimitiveId "equals") && intEquals.InputTypes = [ IrInt; IrInt ])
    check "polymorphic equals is independently instantiated as String" (stringEquals.ResolvedTarget = PrimitiveTarget(PrimitiveId "equals") && stringEquals.InputTypes = [ IrString; IrString ])
    check "map has a concrete Int-to-String callback and List<String> result" (operations "list-to-text" |> List.exists (function | IrOperation.ListMap(call, IrInt, IrString) when call.ResolvedTarget = PrimitiveTarget(PrimitiveId "int.to-string") -> true | _ -> false))
    check "filter has a concrete Bool callback and List<Int> result" (operations "list-positive" |> List.exists (function | IrOperation.ListFilter(call, IrInt) when call.InputTypes = [ IrInt ] && call.OutputTypes = [ IrBool ] -> true | _ -> false))
    check "each retains callback effects even for an empty list" (functionByName "list-print" |> fun fn -> fn.FunctionInferredEffects = Set.singleton IrEffect.ConsoleWrite)
    check "compiler lowers the fold callback to a stable static identity and preserves closed nominal stack types"
        (operations "customers.fold-email"
         |> List.exists (function
             | IrOperation.ListFold(call, IrNominal itemKey, IrNominal accumulatorKey) ->
                 call.ResolvedTarget = UserWordTarget(wordId "customer.fold-email-step", 1)
                 && call.InputTypes = [ IrNominal accumulatorKey; IrNominal itemKey ]
                 && call.OutputTypes = [ IrNominal accumulatorKey ]
                 && itemKey <> accumulatorKey
             | _ -> false))
    check "compiler exposes both fold coverage branch outcomes"
        (executable.CoverageByWord[wordId "customers.fold-email"].BranchOutcomes
         |> Map.exists (fun _ outcomes -> outcomes = [ "empty"; "nonempty" ]))
    check "if join lowers both branches and preserves source coverage" (functionByName "customer.active?" |> fun fn -> fn.FunctionBody.Code |> List.exists (fun instruction -> match instruction.Operation with | IrOperation.If _ -> true | _ -> false) && executable.CoverageByWord[wordId "customer.active?"].BranchOutcomes.Count = 1)
    check "same local name across both if arms resolves to one slot" (operations "local-join" |> List.choose (function | IrOperation.StoreLocal slot | IrOperation.LoadLocal slot -> Some slot | _ -> None) |> List.distinct |> List.length = 1)
    let optionMatch = operations "option-value" |> List.choose (function | IrOperation.MatchOption(slot, _, _) -> Some slot | _ -> None) |> List.exactlyOne
    let resultMatches = operations "result-value" |> List.choose (function | IrOperation.MatchResult(okSlot, errorSlot, _, _) -> Some(okSlot, errorSlot) | _ -> None)
    check "Option and Result branches lower with payload slots" (not (List.isEmpty resultMatches) && (let okSlot, errorSlot = List.exactlyOne resultMatches in okSlot <> errorSlot) && functionByName "option-value" |> fun fn -> Map.containsKey optionMatch fn.LocalNames)
    check "all closed container constructors preserve exact type arguments" (operations "container-constructors" |> List.choose (function | IrOperation.ListEmpty IrInt | IrOperation.ListSingleton IrInt | IrOperation.OptionNone IrInt | IrOperation.OptionSome IrInt | IrOperation.ResultOk(IrString, IrInt) | IrOperation.ResultError(IrString, IrInt) -> Some true | _ -> None) |> List.length = 6)
    let usesGeneratedRecordTarget =
        operations "customer-build"
        |> List.exists (function
            | IrOperation.MakeRecord(call, _) ->
                match call.ResolvedTarget with
                | GeneratedWordTarget _ -> true
                | _ -> false
            | _ -> false)
    let usesGeneratedScalarTarget =
        operations "email-wrap"
        |> List.exists (function
            | IrOperation.WrapScalar(call, _, None) ->
                match call.ResolvedTarget with
                | GeneratedWordTarget _ -> true
                | _ -> false
            | _ -> false)
    check "generated record and scalar operations use resolved stable targets" (usesGeneratedRecordTarget && usesGeneratedScalarTarget)
    check "declared effects include both branches of a conditional" (functionByName "branch-read" |> fun fn -> fn.FunctionInferredEffects = Set.singleton IrEffect.FileRead)

    let detached = Compiler.compileIrBodyAgainstProgram context verified "<agent-eval>" [ TInt ] [ Push(LInt 4L, line 130); Call("add", line 131) ]
    check "detached body retains the exact verified snapshot object" (Object.ReferenceEquals(VerifiedIrBody.program detached, verified))
    check "detached body has its own ownerless source map and coverage" (VerifiedIrBody.inspect detached |> fun body -> body.BodySourceMap.Count = 2 && body.BodyCoverage.CoveredSites.Count = 2 && body.BodySourceMap |> Map.forall (fun site source -> (let (SourceSiteId(owner, _)) = site in owner.IsNone) && source.SiteOwner.IsNone))
    let throwingTest: TestDefinition =
        { Name = "division-error"
          Word = "math"
          Body = [ Push(LInt 1L, line 132); Push(LInt 0L, line 133); Call("divide", line 134) ]
          Expected = ExpectedRuntimeError "RUNTIME_DIVIDE_BY_ZERO"
          EffectAssertion = None
          SourceText = "test division-error"
          Span = line 132 }
    let compiledTest = Compiler.compileIrTestAgainstProgram context verified throwingTest |> VerifiedIrBody.inspect
    check "expected-runtime-error test wrapper keeps a fully typed body" (compiledTest.BodyOutputTypes = [ IrInt ] && compiledTest.BodyBlock.Code.Length = 3)
    let example =
        { Name = "one"
          Word = "math"
          Body = [ Push(LInt 1L, line 135) ]
          Expected = LInt 1L
          SourceText = "example one"
          Span = line 135 }
    check "example wrapper keeps value-checking stack semantics" (Compiler.compileIrExampleAgainstProgram context verified example |> VerifiedIrBody.inspect |> fun body -> body.BodyOutputTypes = [ IrInt ])

    let equalsEntry = Compiler.primitives["equals"]
    let aliasDefinition = { equalsEntry.Definition with Name = "value.same?"; SourceText = "word value.same?" }
    let aliasEntry = { equalsEntry with Definition = aliasDefinition }
    let aliasCaller = wordEntry "equal-alias" [ TInt; TInt ] [ TBool ] pureEffects [ Call("value.same?", line 136) ] 1 Candidate
    let aliasContext = loweringContext (Map.ofList [ "value.same?", aliasEntry; "equal-alias", aliasCaller ]) Map.empty Map.empty
    let aliasProgram = Compiler.compileIrProgram aliasContext |> VerifiedIrProgram.inspect
    let aliasFunction = aliasProgram.FunctionsById[aliasContext.WordIds["equal-alias"]]
    match aliasFunction.FunctionBody.Code with
    | [ { Operation = IrOperation.Call call } ] -> check "primitive alias dispatches by BuiltinOp ID" (call.ResolvedName = "value.same?" && call.ResolvedTarget = PrimitiveTarget(PrimitiveId "equals"))
    | _ -> failwith "primitive alias did not lower to one call"
    let badAlias = { aliasEntry with Definition = { aliasDefinition with Effects = Set.singleton "process.execute" } }
    let badAliasContext = loweringContext (Map.ofList [ "value.same?", badAlias; "equal-alias", aliasCaller ]) Map.empty Map.empty
    expectDiagnostic "primitive alias cannot forge effects" "IR_PRIMITIVE_METADATA_MISMATCH" (fun () -> Compiler.compileIrProgram badAliasContext |> ignore)
    let spoofDefinition = { aliasDefinition with Name = "evil.delete"; Effects = Set.empty }
    let spoofEntry = { aliasEntry with Definition = spoofDefinition; Builtin = Some(BuiltinOp "System.IO.File.Delete") }
    let spoofCaller = wordEntry "try-delete" [ TString ] [ TUnit ] pureEffects [ Push(LUnit, line 137) ] 1 Candidate
    let spoofContext = loweringContext (Map.ofList [ "evil.delete", spoofEntry; "try-delete", spoofCaller ]) Map.empty Map.empty
    expectDiagnostic "unknown process-like BuiltinOp cannot enter the executable catalog" "IR_UNKNOWN_PRIMITIVE_ID" (fun () -> Compiler.compileIrProgram spoofContext |> ignore)

    let runtimeErrorExpectedContext = context
    check "fingerprint regression fixture keeps candidate ID and revision fixed" (runtimeErrorExpectedContext.WordIds["local-join"] = wordId "local-join" && (functionByName "local-join").FunctionRevision = 1)

let private testAttachedSourceOrigins () =
    let file = "attached-flow.ir"
    let authored line = sourceSpan file line
    let privateMarker (origin: SourceSpan) ordinal =
        { origin with Column = Int32.MaxValue - ordinal; Length = 0 }
    let scopeBody marker valueSpan value = [ Scope([ Push(LInt value, valueSpan) ], marker) ]
    let siteSpans (body: VerifiedIrBody) =
        VerifiedIrBody.inspect body
        |> fun inspected -> inspected.BodySourceMap |> Map.toList |> List.map (fun (_, site) -> site.SiteSpan) |> Set.ofList
    let sourceValues (sources: Map<SourceSiteId, IrSourceSite>) = sources |> Map.toList |> List.map snd
    let allAuthored (sources: IrSourceSite list) =
        sources
        |> List.forall (fun site -> site.SiteSpan.Length > 0 && site.SiteSpan.Column < Int32.MaxValue - 1000)

    let contextOrigin = authored 10
    let contextMarker = privateMarker contextOrigin 1
    let contextValueSpan = authored 11
    let contextWord =
        wordEntry "flow-base" [] [ TInt ] Set.empty
            [ Scope([ Push(LInt 5L, contextValueSpan) ], contextMarker) ] 1 Candidate
    let context = loweringContext (Map.ofList [ "flow-base", contextWord ]) Map.empty Map.empty
    let contextOrigins = Map.ofList [ contextMarker, contextOrigin ]
    let verified = Compiler.compileIrProgramWithSourceOrigins context contextOrigins
    let verifiedData = VerifiedIrProgram.inspect verified
    check "Flow-like context Scope markers are remapped before program verification"
        (allAuthored (sourceValues verifiedData.SourceMap) && (sourceValues verifiedData.SourceMap |> List.exists (fun site -> site.SiteSpan = contextOrigin)))

    let actualOrigin = authored 20
    let actualMarker = privateMarker actualOrigin 2
    let actualValueSpan = authored 21
    let expectedOrigin = authored 30
    let expectedMarker = privateMarker expectedOrigin 3
    let expectedValueSpan = authored 31
    let attachedTest: TestDefinition =
        { Name = "scope-value"
          Word = "flow-base"
          Body = scopeBody actualMarker actualValueSpan 7L
          Expected = ExpectedExpression(scopeBody expectedMarker expectedValueSpan 7L)
          EffectAssertion = None
          SourceText = "test scope-value"
          Span = authored 19 }
    let testOrigins = Map.ofList [ contextMarker, contextOrigin; actualMarker, actualOrigin; expectedMarker, expectedOrigin ]
    let actualBody, expectedBodyOption =
        Compiler.compileIrTestWithExpectationAgainstProgramWithSourceOrigins context verified attachedTest testOrigins
    let expectedBody = expectedBodyOption |> Option.defaultWith (fun () -> failwith "Expected Flow value body was not compiled.")
    check "origin-aware test and expectation remain attached to the exact verified snapshot"
        (Object.ReferenceEquals(VerifiedIrBody.program actualBody, verified) && Object.ReferenceEquals(VerifiedIrBody.program expectedBody, verified))
    check "actual Flow-like Scope and authored sites are the only actual-body spans"
        (siteSpans actualBody = Set.ofList [ actualOrigin; actualValueSpan ] && allAuthored (VerifiedIrBody.inspect actualBody |> fun body -> sourceValues body.BodySourceMap))
    check "expected Flow-like Scope and authored sites are the only expectation spans"
        (siteSpans expectedBody = Set.ofList [ expectedOrigin; expectedValueSpan ] && allAuthored (VerifiedIrBody.inspect expectedBody |> fun body -> sourceValues body.BodySourceMap))
    check "actual and expected source-origin bodies execute separately through verified IR"
        (IrInterpreter.executeBody (noOpIrHost ()) "flow-test-body" actualBody = [ IntValue 7L ] &&
         IrInterpreter.executeBody (noOpIrHost ()) "flow-test-expectation" expectedBody = [ IntValue 7L ])

    let exampleOrigin = authored 40
    let exampleMarker = privateMarker exampleOrigin 4
    let exampleValueSpan = authored 41
    let example: ExampleDefinition =
        { Name = "scope-example"
          Word = "flow-base"
          Body = scopeBody exampleMarker exampleValueSpan 9L
          Expected = LInt 9L
          SourceText = "example scope-example"
          Span = authored 39 }
    let exampleOrigins = Map.ofList [ contextMarker, contextOrigin; exampleMarker, exampleOrigin ]
    let compiledExample = Compiler.compileIrExampleAgainstProgramWithSourceOrigins context verified example exampleOrigins
    check "origin-aware example keeps its program binding and remaps every private scope site"
        (Object.ReferenceEquals(VerifiedIrBody.program compiledExample, verified) &&
         siteSpans compiledExample = Set.ofList [ exampleOrigin; exampleValueSpan ] &&
         allAuthored (VerifiedIrBody.inspect compiledExample |> fun body -> sourceValues body.BodySourceMap))
    check "origin-aware example executes through verified IR" (IrInterpreter.executeBody (noOpIrHost ()) "flow-example" compiledExample = [ IntValue 9L ])

    expectDiagnosticRedactsMarkers "attached test origin map rejects a missing expected marker" "IR_SOURCE_ORIGIN_MISSING" [ expectedMarker ] (fun () ->
        Compiler.compileIrTestWithExpectationAgainstProgramWithSourceOrigins context verified attachedTest (Map.remove expectedMarker testOrigins) |> ignore)

    let extraOrigin = authored 60
    let extraMarker = privateMarker extraOrigin 60
    expectDiagnosticRedactsMarkers "attached test origin map rejects an unrelated marker" "IR_SOURCE_ORIGIN_SET_MISMATCH" [ extraMarker ] (fun () ->
        Compiler.compileIrTestWithExpectationAgainstProgramWithSourceOrigins context verified attachedTest (Map.add extraMarker extraOrigin testOrigins) |> ignore)

    let testWithActualExpectedCollision =
        { attachedTest with Expected = ExpectedExpression(scopeBody actualMarker expectedValueSpan 7L) }
    let actualExpectedOrigins = Map.ofList [ contextMarker, contextOrigin; actualMarker, actualOrigin ]
    expectDiagnosticRedactsMarkers "attached test rejects actual-to-expectation marker reuse" "IR_SOURCE_ORIGIN_MARKER_COLLISION" [ actualMarker ] (fun () ->
        Compiler.compileIrTestWithExpectationAgainstProgramWithSourceOrigins context verified testWithActualExpectedCollision actualExpectedOrigins |> ignore)

    let testWithContextActualCollision = { attachedTest with Body = scopeBody contextMarker actualValueSpan 7L }
    let contextActualOrigins = Map.ofList [ contextMarker, contextOrigin; expectedMarker, expectedOrigin ]
    expectDiagnosticRedactsMarkers "attached test rejects context-to-actual marker reuse" "IR_SOURCE_ORIGIN_MARKER_COLLISION" [ contextMarker ] (fun () ->
        Compiler.compileIrTestWithExpectationAgainstProgramWithSourceOrigins context verified testWithContextActualCollision contextActualOrigins |> ignore)

    let testWithContextExpectedCollision =
        { attachedTest with Expected = ExpectedExpression(scopeBody contextMarker expectedValueSpan 7L) }
    let contextExpectedOrigins = Map.ofList [ contextMarker, contextOrigin; actualMarker, actualOrigin ]
    expectDiagnosticRedactsMarkers "attached test rejects context-to-expectation marker reuse" "IR_SOURCE_ORIGIN_MARKER_COLLISION" [ contextMarker ] (fun () ->
        Compiler.compileIrTestWithExpectationAgainstProgramWithSourceOrigins context verified testWithContextExpectedCollision contextExpectedOrigins |> ignore)

    let changedContextWord =
        { contextWord with Definition = { contextWord.Definition with Body = [ Scope([ Push(LInt 6L, contextValueSpan) ], contextMarker) ] } }
    let changedContext = { context with Words = Map.add "flow-base" changedContextWord context.Words }
    expectDiagnosticRedactsMarkers "attached test still fails closed for a stale fingerprint" "IR_STALE_COMPILER_SNAPSHOT" [ contextMarker ] (fun () ->
        Compiler.compileIrTestWithExpectationAgainstProgramWithSourceOrigins changedContext verified attachedTest testOrigins |> ignore)

    let badActualScopeOrigin = authored 70
    let badActualScopeMarker = privateMarker badActualScopeOrigin 70
    let badActualLoadOrigin = authored 71
    let badActualLoadMarker = privateMarker badActualLoadOrigin 71
    let badActualTest =
        { attachedTest with
            Name = "missing-actual-local"
            Body = [ Scope([ Load("$flow$missing", badActualLoadMarker) ], badActualScopeMarker) ]
            Expected = ExpectedRuntimeError "NAME_UNKNOWN_LOCAL" }
    let badActualOrigins = Map.ofList [ contextMarker, contextOrigin; badActualScopeMarker, badActualScopeOrigin; badActualLoadMarker, badActualLoadOrigin ]
    expectDiagnosticDetails "actual-body type errors map private markers to authored spans without changing payload" "NAME_UNKNOWN_LOCAL"
        (Some badActualLoadOrigin) [] [ "$flow$missing" ] (fun () ->
            Compiler.compileIrTestWithExpectationAgainstProgramWithSourceOrigins context verified badActualTest badActualOrigins |> ignore)

    let badExpectedScopeOrigin = authored 72
    let badExpectedScopeMarker = privateMarker badExpectedScopeOrigin 72
    let badExpectedLoadOrigin = authored 73
    let badExpectedLoadMarker = privateMarker badExpectedLoadOrigin 73
    let badExpectedTest =
        { attachedTest with
            Name = "missing-expected-local"
            Expected = ExpectedExpression([ Scope([ Load("$flow$expected-missing", badExpectedLoadMarker) ], badExpectedScopeMarker) ]) }
    let badExpectedOrigins =
        Map.ofList [ contextMarker, contextOrigin; actualMarker, actualOrigin;
                     badExpectedScopeMarker, badExpectedScopeOrigin; badExpectedLoadMarker, badExpectedLoadOrigin ]
    expectDiagnosticDetails "expectation type errors map private markers to authored spans without changing payload" "NAME_UNKNOWN_LOCAL"
        (Some badExpectedLoadOrigin) [] [ "$flow$expected-missing" ] (fun () ->
            Compiler.compileIrTestWithExpectationAgainstProgramWithSourceOrigins context verified badExpectedTest badExpectedOrigins |> ignore)

    let badExampleScopeOrigin = authored 74
    let badExampleScopeMarker = privateMarker badExampleScopeOrigin 74
    let badExampleLoadOrigin = authored 75
    let badExampleLoadMarker = privateMarker badExampleLoadOrigin 75
    let badExample =
        { example with
            Name = "missing-example-local"
            Body = [ Scope([ Load("$flow$example-missing", badExampleLoadMarker) ], badExampleScopeMarker) ] }
    let badExampleOrigins = Map.ofList [ contextMarker, contextOrigin; badExampleScopeMarker, badExampleScopeOrigin; badExampleLoadMarker, badExampleLoadOrigin ]
    expectDiagnosticDetails "example type errors map private markers to authored spans without changing payload" "NAME_UNKNOWN_LOCAL"
        (Some badExampleLoadOrigin) [] [ "$flow$example-missing" ] (fun () ->
            Compiler.compileIrExampleAgainstProgramWithSourceOrigins context verified badExample badExampleOrigins |> ignore)

    let effectfulExpectedOrigin = authored 80
    let effectfulExpectedMarker = privateMarker effectfulExpectedOrigin 80
    let effectfulExpected: TestDefinition =
        { attachedTest with
            Name = "effectful-expected"
            Expected = ExpectedExpression([ Scope([ Push(LString "unexpected", authored 81); Call("console.write", authored 82) ], effectfulExpectedMarker) ]) }
    let effectfulOrigins = Map.ofList [ contextMarker, contextOrigin; actualMarker, actualOrigin; effectfulExpectedMarker, effectfulExpectedOrigin ]
    expectDiagnosticRedactsMarkers "origin-aware attached tests preserve the pure-expectation gate" "TEST_EXPECTED_VALUE_EFFECTS" [ effectfulExpectedMarker ] (fun () ->
        Compiler.compileIrTestWithExpectationAgainstProgramWithSourceOrigins context verified effectfulExpected effectfulOrigins |> ignore)

    let plainContext = loweringContext Map.empty Map.empty Map.empty
    let plainProgram = Compiler.compileIrProgram plainContext
    let plainTest: TestDefinition =
        { Name = "plain-value"
          Word = "plain"
          Body = [ Push(LInt 2L, authored 90); Push(LInt 3L, authored 91); Call("add", authored 92) ]
          Expected = ExpectedExpression([ Push(LInt 5L, authored 93) ])
          EffectAssertion = None
          SourceText = "test plain-value"
          Span = authored 89 }
    let inspectTestPair (actual, expected) =
        VerifiedIrBody.inspect actual, expected |> Option.map VerifiedIrBody.inspect
    let legacyTestPair = Compiler.compileIrTestWithExpectationAgainstProgram plainContext plainProgram plainTest
    let emptyOriginTestPair = Compiler.compileIrTestWithExpectationAgainstProgramWithSourceOrigins plainContext plainProgram plainTest Map.empty
    check "empty-origin attached test API matches the legacy context snapshot" (inspectTestPair legacyTestPair = inspectTestPair emptyOriginTestPair)

    let plainExample =
        { Name = "plain-example"
          Word = "plain"
          Body = [ Push(LInt 4L, authored 94) ]
          Expected = LInt 4L
          SourceText = "example plain-example"
          Span = authored 94 }
    let legacyPlainExample = Compiler.compileIrExampleAgainstProgram plainContext plainProgram plainExample
    let emptyOriginPlainExample = Compiler.compileIrExampleAgainstProgramWithSourceOrigins plainContext plainProgram plainExample Map.empty
    check "empty-origin attached example API matches the legacy context snapshot"
        (VerifiedIrBody.inspect legacyPlainExample = VerifiedIrBody.inspect emptyOriginPlainExample)

let private testCompilerSnapshotIdentity () =
    let span = testSpan "snapshot.agent" 1
    let stableId = WordId "candidate-stable-id"
    let buildContext (body: Expr list) : Compiler.IrLoweringContext =
        let candidate = wordEntry "candidate" [ TInt ] [ TInt ] Set.empty body 5 Candidate
        let context = loweringContext (Map.ofList [ "candidate", candidate ]) Map.empty Map.empty
        let withStableId: Compiler.IrLoweringContext = { context with WordIds = Map.add "candidate" stableId context.WordIds }
        withStableId
    let repeatedBody changedTail =
        [ for index in 0 .. 119 do
              let number = if index = 119 then changedTail else int64 index
              yield Push(LInt number, span)
              yield Call("add", span) ]
    let first = buildContext (repeatedBody 299L)
    let second = buildContext (repeatedBody 999999L)
    let verified = Compiler.compileIrProgram first
    expectDiagnostic "same-ID same-revision long candidate body cannot reuse stale IR" "IR_STALE_COMPILER_SNAPSHOT" (fun () -> Compiler.compileIrBodyAgainstProgram second verified "eval" [] [] |> ignore)

    let makeRecord lastFieldType =
        let fields =
            [ for index in 0 .. 159 do
                  yield { Name = if index = 159 then "tail" else "field" + string index
                          Type = if index = 159 then lastFieldType else TInt } ]
        { Name = "Wide"
          Fields = fields
          SourceText = "record Wide"
          Span = span }
    let recordContext fieldType = loweringContext Map.empty (Map.ofList [ "Wide", makeRecord fieldType ]) Map.empty
    let recordSnapshot = Compiler.compileIrProgram (recordContext TInt)
    expectDiagnostic "record layout after the hundredth field invalidates cached body" "IR_STALE_COMPILER_SNAPSHOT" (fun () -> Compiler.compileIrBodyAgainstProgram (recordContext TBool) recordSnapshot "eval" [] [] |> ignore)

    let deepType = [ 1 .. 140 ] |> List.fold (fun current _ -> TList current) TInt
    let deepEntry = wordEntry "deep" [ deepType ] [ deepType ] Set.empty [] 1 Candidate
    let deepContext = loweringContext (Map.ofList [ "deep", deepEntry ]) Map.empty Map.empty
    let deepSnapshot = Compiler.compileIrProgram deepContext
    let changedDeepType = [ 1 .. 139 ] |> List.fold (fun current _ -> TList current) TString |> fun item -> TList item
    let changedDeepEntry = wordEntry "deep" [ changedDeepType ] [ changedDeepType ] Set.empty [] 1 Candidate
    let changedDeepContext = loweringContext (Map.ofList [ "deep", changedDeepEntry ]) Map.empty Map.empty
    expectDiagnostic "deep nested signatures are fully fingerprinted" "IR_STALE_COMPILER_SNAPSHOT" (fun () -> Compiler.compileIrBodyAgainstProgram changedDeepContext deepSnapshot "eval" [] [] |> ignore)

    let foldStep name = wordEntry name [ TInt; TInt ] [ TInt ] Set.empty [ Call("add", span) ] 1 Candidate
    let foldOwner callback = wordEntry "fold-owner" [ TList TInt; TInt ] [ TInt ] Set.empty [ FoldList(callback, span) ] 1 Candidate
    let foldContext callback =
        loweringContext (Map.ofList [ callback, foldStep callback; "fold-owner", foldOwner callback ]) Map.empty Map.empty
    let oldFoldContext = foldContext "fold-step-old"
    let newFoldContext = foldContext "fold-step-renamed"
    let foldSnapshot = Compiler.compileIrProgram oldFoldContext
    expectDiagnostic "changing a static fold callback target invalidates the bound compiler snapshot" "IR_STALE_COMPILER_SNAPSHOT"
        (fun () -> Compiler.compileIrBodyAgainstProgram newFoldContext foldSnapshot "fold-owner" [] [] |> ignore)

let private tests =
    [ "closed effect vocabulary", testClosedEffects
      "concrete primitive specializations", testPrimitiveSpecializations
      "closed container constructors", testClosedContainerConstructors
      "structured branches and coverage", testStructuredBranchesAndCoverage
      "closed synthetic source classifications", testClosedSyntheticSourceKinds
      "Option and Result payload scope", testOptionAndResultCaseLocals
      "closed enum verifier invariants", testClosedEnumVerification
      "finite Bool, Unit, Option, Result, and finite record domains", testFiniteCoverageClosedDomains
      "finite independent inputs and nominal enum identity", testFiniteCoverageIndependentInputsAndNominality
      "finite coverage projections in open and nested records", testFiniteCoverageOpenAndNestedRecords
      "finite unsupported and bounded domains", testFiniteCoverageUnsupportedAndBoundedDomains
      "finite coverage rejects malformed generic metadata", testFiniteCoverageRejectsMalformedGenericMetadata
      "generated record and scalar operations", testGeneratedRecordAndScalarOperations
      "static callbacks and effects", testStaticCallbacksAndEffects
      "typed list fold verification", testListFoldVerification
      "identity and call-graph guards", testIdentityAndCallGraphGuards
      "flat verifier stack safety", testFlatVerifierStackSafety
      "compiler lowering and detached bodies", testCompilerLowering
      "attached Flow source origins", testAttachedSourceOrigins
      "compiler snapshot identity", testCompilerSnapshotIdentity ]

[<EntryPoint>]
let main _ =
    try
        for name, test in tests do
            test ()
            printfn "PASS %s" name
        printfn "AgentLang.IR.Tests: %d assertions passed." assertions
        0
    with error ->
        eprintfn "%s" error.Message
        1
