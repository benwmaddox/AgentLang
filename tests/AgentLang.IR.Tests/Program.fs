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

let private noEffects = Set.empty<IrEffect>

let private sourceSpan file line =
    { File = file
      Line = line
      Column = 1
      Length = 1 }

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

let private tests =
    [ "closed effect vocabulary", testClosedEffects
      "concrete primitive specializations", testPrimitiveSpecializations
      "closed container constructors", testClosedContainerConstructors
      "structured branches and coverage", testStructuredBranchesAndCoverage
      "Option and Result payload scope", testOptionAndResultCaseLocals
      "generated record and scalar operations", testGeneratedRecordAndScalarOperations
      "static callbacks and effects", testStaticCallbacksAndEffects
      "identity and call-graph guards", testIdentityAndCallGraphGuards ]

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
