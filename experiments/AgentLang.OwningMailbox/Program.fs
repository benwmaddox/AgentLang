module AgentLang.OwningMailbox.Program

open System
open System.IO
open System.Text.Json
open AgentLang
open AgentLang.Llvm

type private MailboxBodies =
    { Program: VerifiedIrProgram
      CompilerContext: Compiler.IrLoweringContext
      SourceOrigins: Map<SourceSpan, SourceSpan>
      SourceTypeIds: Map<string, uint32>
      ScalarDefinitions: Map<string, ScalarTypeDefinition>
      Initialize: VerifiedIrBody
      Begin: VerifiedIrBody
      Resume: VerifiedIrBody }

let private sourceSpan file column =
    { File = file
      Line = 1
      Column = column
      Length = 1 }

let private lowerFirst (name: string) =
    if String.IsNullOrEmpty name then name
    else string (Char.ToLowerInvariant name[0]) + name.Substring(1)

let private generatedRecordEntries (records: Map<string, RecordDefinition>) =
    records
    |> Map.toList
    |> List.collect (fun (name, record) ->
        let prefix = lowerFirst name
        let definition wordName inputs outputs =
            { Name = wordName
              Inputs = inputs
              Outputs = outputs
              Effects = Set.empty
              Maturity = LibraryWord
              Revision = 1
              Documentation = "Generated owning-mailbox record operation."
              Body = []
              SourceText = ""
              Span = sourceSpan "<generated-owning-mailbox-record>" 1 }
        let entry builtin wordName inputs outputs =
            { Definition = definition wordName inputs outputs
              Builtin = Some builtin
              Status = Persistent
              Maturity = LibraryWord
              Revision = 1 }
        let constructor =
            entry (RecordConstructor name) (prefix + ".new")
                (record.Fields |> List.map (fun field -> field.Type)) [ TNamed name ]
        let accessors =
            record.Fields
            |> List.map (fun field ->
                entry (RecordAccessor(name, field.Name)) (prefix + "." + field.Name)
                    [ TNamed name ] [ field.Type ])
        constructor :: accessors)

let private generatedScalarEntries (scalars: Map<string, ScalarTypeDefinition>) =
    scalars
    |> Map.toList
    |> List.collect (fun (name, scalar) ->
        let prefix = lowerFirst name
        let definition wordName inputs outputs =
            { Name = wordName
              Inputs = inputs
              Outputs = outputs
              Effects = Set.empty
              Maturity = LibraryWord
              Revision = 1
              Documentation = "Generated owning-mailbox scalar operation."
              Body = []
              SourceText = ""
              Span = scalar.Span }
        let entry builtin wordName inputs outputs =
            { Definition = definition wordName inputs outputs
              Builtin = Some builtin
              Status = Persistent
              Maturity = LibraryWord
              Revision = 1 }
        [ entry (ScalarConstructor name) (prefix + ".new") [ scalar.BaseType ] [ TNamed name ]
          entry (ScalarAccessor name) (prefix + ".value") [ TNamed name ] [ scalar.BaseType ] ])

let private compileBodies (sourceName: string) (source: string) =
    let document =
        match FlowParser.parseDocumentWithVersion 2 sourceName source with
        | Ok parsed -> parsed
        | Error diagnostic -> failwith (Diagnostics.render diagnostic)
    let records = document.Records |> List.map (fun record -> record.Name, record) |> Map.ofList
    let scalars = document.Scalars |> List.map (fun scalar -> scalar.Name, scalar) |> Map.ofList
    let words =
        generatedRecordEntries records @ generatedScalarEntries scalars
        |> List.fold (fun current entry -> Map.add entry.Definition.Name entry current) Compiler.primitives
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
    let compilerContext: Compiler.IrLoweringContext =
        { Words = words
          Records = records
          Scalars = scalars
          Enums = Map.empty
          WordIds = wordIds }
    let context: FlowLowering.Context =
        { CompilerContext = compilerContext
          ParameterNames = Map.empty
          Flow2OwnerIds = Set.empty
          SourceOrigins = Map.empty }
    let changes: FlowLowering.FlowWordChange list =
        document.Words
        |> List.map (fun definition ->
            { FlowLowering.FlowWordChange.Definition = definition
              RevisionIntent = FlowLowering.FlowWordRevisionIntent.Add(WordId("owning-mailbox-source-" + definition.Name), 1) })
    let compiled = FlowLowering.compileBatchWords context changes
    let compileEntry entryName target inputTypes =
        Compiler.compileIrBodyAgainstProgramWithSourceOrigins
            compiled.Context.CompilerContext compiled.Program entryName inputTypes
            [ Call(target, sourceSpan ("<" + entryName + ">") 1) ]
            compiled.Context.SourceOrigins
    let nominalTypeNames = (records |> Map.toList |> List.map fst) @ (scalars |> Map.toList |> List.map fst)
    let nominalTypeIds =
        nominalTypeNames
        |> List.sortWith (fun left right -> StringComparer.Ordinal.Compare(left, right))
        |> List.mapi (fun index name -> name, uint32 (index + 4))
    let bodies =
        { Program = compiled.Program
          CompilerContext = compiled.Context.CompilerContext
          SourceOrigins = compiled.Context.SourceOrigins
          SourceTypeIds =
            nominalTypeIds
            |> List.append [ "Int", 1u; "Bool", 2u; "Unit", 3u ]
            |> fun values -> values @ [ "String", uint32 (nominalTypeNames.Length + 4) ]
            |> Map.ofList
          ScalarDefinitions = scalars
          Initialize = compileEntry "owning-mailbox.initialize.entry" "mailbox.initialize" [ TString ]
          Begin = compileEntry "owning-mailbox.begin.entry" "mailbox.begin" [ TNamed "State"; TString ]
          Resume = compileEntry "owning-mailbox.resume.entry" "mailbox.resume" [ TNamed "State"; TNamed "Continuation"; TString ] }
    let roles = [ bodies.Initialize; bodies.Begin; bodies.Resume ]
    if not (VerifiedIrProgram.isBackendExecutable bodies.Program) then
        invalidOp "Owning-mailbox lowering did not produce a backend-authorized VerifiedIrProgram."
    if roles |> List.exists (fun body -> not (Object.ReferenceEquals(VerifiedIrBody.program body, bodies.Program))) then
        invalidOp "Owning-mailbox role entries do not share one VerifiedIrProgram instance."
    bodies

let private interpreterHost (compilerContext: Compiler.IrLoweringContext) =
    let wordSpans = compilerContext.Words |> Map.map (fun _ entry -> entry.Definition.Span)
    { PreflightEffects = fun _ _ _ -> ()
      ChargeInstruction = fun _ _ -> ()
      RecordBranchOutcome = fun _ _ _ -> ()
      RecordUse = ignore
      InvokeEffect = fun _ -> EffectUnit
      EnterUserFunction = fun _ _ _ -> fun () -> ()
      ReturnUserFunction = fun _ _ _ -> ()
      WordDefinitionSpan = fun word -> wordSpans.TryFind word
      PrimitiveDefinitionSpan = fun word -> wordSpans.TryFind word }

let private interpreterHostWithStepCounter (compilerContext: Compiler.IrLoweringContext) (stepCount: int ref) =
    let wordSpans = compilerContext.Words |> Map.map (fun _ entry -> entry.Definition.Span)
    { PreflightEffects = fun _ _ _ -> ()
      ChargeInstruction = fun _ _ -> stepCount.Value <- stepCount.Value + 1
      RecordBranchOutcome = fun _ _ _ -> ()
      RecordUse = ignore
      InvokeEffect = fun _ -> EffectUnit
      EnterUserFunction = fun _ _ _ -> fun () -> ()
      ReturnUserFunction = fun _ _ _ -> ()
      WordDefinitionSpan = fun word -> wordSpans.TryFind word
      PrimitiveDefinitionSpan = fun word -> wordSpans.TryFind word }

let rec private blockInstructionCount (block: IrBlock) =
    block.Code
    |> List.sumBy (fun instruction ->
        1
        + match instruction.Operation with
          | IrOperation.Scope nested -> blockInstructionCount nested
          | IrOperation.If(thenBlock, elseBlock) -> blockInstructionCount thenBlock + blockInstructionCount elseBlock
          | IrOperation.MatchOption(_, someBlock, noneBlock) -> blockInstructionCount someBlock + blockInstructionCount noneBlock
          | IrOperation.MatchResult(_, _, okBlock, errorBlock) -> blockInstructionCount okBlock + blockInstructionCount errorBlock
          | IrOperation.MatchEnum(_, cases) -> cases |> List.sumBy (snd >> blockInstructionCount)
          | _ -> 0)

let private functionForName (program: VerifiedIrProgram) (name: string) =
    let inspected = VerifiedIrProgram.inspect program
    inspected.FunctionsById
    |> Map.toList
    |> List.map snd
    |> List.tryFind (fun fn -> fn.FunctionName = name)
    |> Option.defaultWith (fun () -> invalidOp $"Verified Flow function '{name}' is missing.")

let private refinedStepOracle (bodies: MailboxBodies) =
    let steps = ref 0
    let host = interpreterHostWithStepCounter bodies.CompilerContext steps
    let program = bodies.Program
    let sourceOrigins = bodies.SourceOrigins
    let compileString executionName value =
        Compiler.compileIrBodyAgainstProgramWithSourceOrigins bodies.CompilerContext program executionName []
            [ Push(LString value, sourceSpan ("<" + executionName + ">") 1) ] sourceOrigins
    use seed = IrInterpreter.executeBodyWithInputs host "owning-mailbox.refinement-oracle.seed" (compileString "refinement-oracle-seed" "A") None []
    steps.Value <- 0
    use initial = IrInterpreter.executeBodyWithInputs host "owning-mailbox.initialize" bodies.Initialize (Some seed) [ IrEntryArgument.RetainedRoot 0 ]
    let initializeBodySteps = steps.Value
    let beginInput =
        Compiler.compileIrBodyAgainstProgramWithSourceOrigins bodies.CompilerContext program "owning-mailbox.refinement-oracle.begin-input" [ TNamed "State" ]
            [ Push(LString "B", sourceSpan "<refinement-oracle-begin>" 1) ] sourceOrigins
    steps.Value <- 0
    use beginArguments = IrInterpreter.executeBodyWithInputs host "owning-mailbox.refinement-oracle.begin-arguments" beginInput (Some initial) [ IrEntryArgument.RetainedRoot 0 ]
    steps.Value <- 0
    use pending = IrInterpreter.executeBodyWithInputs host "owning-mailbox.begin" bodies.Begin (Some beginArguments) [ IrEntryArgument.RetainedRoot 0; IrEntryArgument.RetainedRoot 1 ]
    let beginBodySteps = steps.Value
    let initializedJson = ValueInspection.toJson program (initial.Decode())
    let pendingJson = ValueInspection.toJson program (pending.Decode())
    let resumeInput executionName message =
        Compiler.compileIrBodyAgainstProgramWithSourceOrigins bodies.CompilerContext program executionName [ TNamed "State"; TNamed "Continuation" ]
            [ Push(LString message, sourceSpan ("<" + executionName + ">") 1) ] sourceOrigins
    let runResume executionName message =
        let argumentsBody = resumeInput (executionName + ".arguments") message
        use argumentsOwner = IrInterpreter.executeBodyWithInputs host (executionName + ".arguments") argumentsBody (Some pending) [ IrEntryArgument.RetainedRoot 0; IrEntryArgument.RetainedRoot 1 ]
        steps.Value <- 0
        try
            use completed = IrInterpreter.executeBodyWithInputs host executionName bodies.Resume (Some argumentsOwner) [ IrEntryArgument.RetainedRoot 0; IrEntryArgument.RetainedRoot 1; IrEntryArgument.RetainedRoot 2 ]
            steps.Value, Some(ValueInspection.toJson program (completed.Decode()))
        with _ -> steps.Value, None
    let resumeEmptySteps, _ = runResume "owning-mailbox.resume-empty" ""
    let resumeFailSteps, _ = runResume "owning-mailbox.resume-fail" "FAIL"
    // Use exact retained roots and change only the message argument for successful C.
    let resumeSuccessSteps, completedJson = runResume "owning-mailbox.resume-success" "C"
    let positiveValidator = functionForName program "mailbox.is-positive-id?"
    let stringValidator = functionForName program "mailbox.is-non-empty?"
    let positiveCost = blockInstructionCount positiveValidator.FunctionBody
    let stringCost = blockInstructionCount stringValidator.FunctionBody
    let admissionCost positiveCount stringCount = positiveCount * positiveCost + stringCount * stringCost
    let expectedStep handlerSteps positiveCount stringCount = handlerSteps + admissionCost positiveCount stringCount
    {| validatorIdentity =
           [ {| name = "mailbox.is-positive-id?"
                wordId = (match positiveValidator.FunctionId with WordId value -> value)
                revision = positiveValidator.FunctionRevision
                instructionCost = positiveCost |};
             {| name = "mailbox.is-non-empty?"
                wordId = (match stringValidator.FunctionId with WordId value -> value)
                revision = stringValidator.FunctionRevision
                instructionCost = stringCost |}]
       bodySteps = {| initialize = initializeBodySteps; beginRole = beginBodySteps; resumeEmpty = resumeEmptySteps; resumeFail = resumeFailSteps; resumeSuccess = resumeSuccessSteps |}
       interpreterValues = {| initialized = initializedJson; pending = pendingJson; completed = completedJson |}
       expectedCalls =
         {| initialize = {| positiveId = 1; nonEmptyString = 1 |}
            beginRole = {| positiveId = 3; nonEmptyString = 2 |}
            resumeEmptyReturn = {| positiveId = 4; nonEmptyString = 4 |}
            resumeEmptyKeep = {| positiveId = 1; nonEmptyString = 1 |}
            resumeFailReturn = {| positiveId = 3; nonEmptyString = 3 |}
            resumeFailKeep = {| positiveId = 0; nonEmptyString = 0 |}
            resumeSuccessReturn = {| positiveId = 4; nonEmptyString = 4 |}
            resumeSuccessKeep = {| positiveId = 1; nonEmptyString = 1 |}
            controllerTotalReturn = {| positiveId = 15; nonEmptyString = 14 |}
            controllerTotalKeep = {| positiveId = 6; nonEmptyString = 5 |} |}
       expectedSteps =
         {| initialize = initializeBodySteps
            beginReturn = expectedStep beginBodySteps 2 1
            beginKeep = expectedStep beginBodySteps 2 1
            resumeEmptyReturn = expectedStep resumeEmptySteps 3 3
            resumeEmptyKeep = resumeEmptySteps
            resumeFailReturn = expectedStep resumeFailSteps 3 3
            resumeFailKeep = resumeFailSteps
            resumeSuccessReturn = expectedStep resumeSuccessSteps 3 3
            resumeSuccessKeep = resumeSuccessSteps
            returnTotal = initializeBodySteps + expectedStep beginBodySteps 2 1 + expectedStep resumeEmptySteps 3 3 + expectedStep resumeFailSteps 3 3 + expectedStep resumeSuccessSteps 3 3
            keepTotal = initializeBodySteps + expectedStep beginBodySteps 2 1 + resumeEmptySteps + resumeFailSteps + resumeSuccessSteps |} |}

let private interpreterOracle (bodies: MailboxBodies) seed chunk message =
    let context = bodies.CompilerContext
    let program = bodies.Program
    let host = interpreterHost context
    let stringBody name value =
        Compiler.compileIrBodyAgainstProgramWithSourceOrigins context program name []
            [ Push(LString value, sourceSpan ("<" + name + ">") 1) ]
            bodies.SourceOrigins
    let beginInputs =
        Compiler.compileIrBodyAgainstProgramWithSourceOrigins context program "owning-mailbox.oracle.begin-inputs" [ TNamed "State" ]
            [ Push(LString chunk, sourceSpan "<oracle-chunk>" 1) ]
            bodies.SourceOrigins
    let resumeInputs =
        Compiler.compileIrBodyAgainstProgramWithSourceOrigins context program "owning-mailbox.oracle.resume-inputs" [ TNamed "State"; TNamed "Continuation" ]
            [ Push(LString message, sourceSpan "<oracle-message>" 1) ]
            bodies.SourceOrigins
    use seedOwner = IrInterpreter.executeBodyWithInputs host "owning-mailbox.oracle.seed" (stringBody "oracle-seed" seed) None []
    use state = IrInterpreter.executeBodyWithInputs host "owning-mailbox.initialize" bodies.Initialize (Some seedOwner) [ IrEntryArgument.RetainedRoot 0 ]
    use initArguments = IrInterpreter.executeBodyWithInputs host "owning-mailbox.oracle.begin-arguments" beginInputs (Some state) [ IrEntryArgument.RetainedRoot 0 ]
    use pending = IrInterpreter.executeBodyWithInputs host "owning-mailbox.begin" bodies.Begin (Some initArguments) [ IrEntryArgument.RetainedRoot 0; IrEntryArgument.RetainedRoot 1 ]
    use resumeArguments = IrInterpreter.executeBodyWithInputs host "owning-mailbox.oracle.resume-arguments" resumeInputs (Some pending) [ IrEntryArgument.RetainedRoot 0; IrEntryArgument.RetainedRoot 1 ]
    use completed = IrInterpreter.executeBodyWithInputs host "owning-mailbox.resume" bodies.Resume (Some resumeArguments) [ IrEntryArgument.RetainedRoot 0; IrEntryArgument.RetainedRoot 1; IrEntryArgument.RetainedRoot 2 ]
    {| initializedJson = ValueInspection.toJson program (state.Decode())
       pendingJson = ValueInspection.toJson program (pending.Decode())
       completedJson = ValueInspection.toJson program (completed.Decode()) |}

let private interpreterErrorRoundTrip (bodies: MailboxBodies) =
    let context = bodies.CompilerContext
    let program = bodies.Program
    let host = interpreterHost context
    let stringBody name value =
        Compiler.compileIrBodyAgainstProgramWithSourceOrigins context program name []
            [ Push(LString value, sourceSpan ("<" + name + ">") 1) ]
            bodies.SourceOrigins
    let beginInputs name chunk =
        Compiler.compileIrBodyAgainstProgramWithSourceOrigins context program name [ TNamed "State" ]
            [ Push(LString chunk, sourceSpan ("<" + name + ">") 1) ]
            bodies.SourceOrigins
    let resumeInputs name message =
        Compiler.compileIrBodyAgainstProgramWithSourceOrigins context program name [ TNamed "State"; TNamed "Continuation" ]
            [ Push(LString message, sourceSpan ("<" + name + ">") 1) ]
            bodies.SourceOrigins
    use seed = IrInterpreter.executeBodyWithInputs host "owning-mailbox.error-round-trip.seed" (stringBody "error-round-trip-seed" "Z") None []
    use initial = IrInterpreter.executeBodyWithInputs host "owning-mailbox.initialize" bodies.Initialize (Some seed) [ IrEntryArgument.RetainedRoot 0 ]
    use firstBeginArguments = IrInterpreter.executeBodyWithInputs host "owning-mailbox.error-round-trip.first-begin-arguments" (beginInputs "error-round-trip-first-chunk" "") (Some initial) [ IrEntryArgument.RetainedRoot 0 ]
    use firstPending = IrInterpreter.executeBodyWithInputs host "owning-mailbox.begin" bodies.Begin (Some firstBeginArguments) [ IrEntryArgument.RetainedRoot 0; IrEntryArgument.RetainedRoot 1 ]
    use firstResumeArguments = IrInterpreter.executeBodyWithInputs host "owning-mailbox.error-round-trip.first-resume-arguments" (resumeInputs "error-round-trip-error-message" "ERR") (Some firstPending) [ IrEntryArgument.RetainedRoot 0; IrEntryArgument.RetainedRoot 1 ]
    use errorState = IrInterpreter.executeBodyWithInputs host "owning-mailbox.resume" bodies.Resume (Some firstResumeArguments) [ IrEntryArgument.RetainedRoot 0; IrEntryArgument.RetainedRoot 1; IrEntryArgument.RetainedRoot 2 ]
    use followupBeginArguments = IrInterpreter.executeBodyWithInputs host "owning-mailbox.error-round-trip.followup-begin-arguments" (beginInputs "error-round-trip-followup-chunk" "") (Some errorState) [ IrEntryArgument.RetainedRoot 0 ]
    use followupPending = IrInterpreter.executeBodyWithInputs host "owning-mailbox.begin" bodies.Begin (Some followupBeginArguments) [ IrEntryArgument.RetainedRoot 0; IrEntryArgument.RetainedRoot 1 ]
    use followupResumeArguments = IrInterpreter.executeBodyWithInputs host "owning-mailbox.error-round-trip.followup-resume-arguments" (resumeInputs "error-round-trip-followup-message" "") (Some followupPending) [ IrEntryArgument.RetainedRoot 0; IrEntryArgument.RetainedRoot 1 ]
    use completed = IrInterpreter.executeBodyWithInputs host "owning-mailbox.resume" bodies.Resume (Some followupResumeArguments) [ IrEntryArgument.RetainedRoot 0; IrEntryArgument.RetainedRoot 1; IrEntryArgument.RetainedRoot 2 ]
    {| errorStateJson = ValueInspection.toJson program (errorState.Decode())
       followupPendingJson = ValueInspection.toJson program (followupPending.Decode())
       completedJson = ValueInspection.toJson program (completed.Decode()) |}

let private parseOptimization = function
    | "O0" -> LlvmOptimization.O0
    | "O2" -> LlvmOptimization.O2
    | value -> invalidArg "optimization" $"Unsupported optimization '{value}'; expected O0 or O2."

let private parseRuntimeProfile = function
    | [] -> OwningRuntimeProfile.Diagnostic
    | [ "--runtime-profile"; "diagnostic" ] -> OwningRuntimeProfile.Diagnostic
    | [ "--runtime-profile"; "trusted-generated" ] -> OwningRuntimeProfile.TrustedGenerated
    | arguments ->
        let shownArguments = String.concat " " arguments
        invalidArg "runtimeProfile" $"Unsupported runtime profile arguments '{shownArguments}'; expected --runtime-profile diagnostic or --runtime-profile trusted-generated."

let private runtimeProfileName = function
    | OwningRuntimeProfile.Diagnostic -> "diagnostic"
    | OwningRuntimeProfile.TrustedGenerated -> "trusted-generated"

let private usage = "Usage: AgentLang.OwningMailbox <O0|O2> <output-directory> <owning-mailbox.flow> [--runtime-profile <diagnostic|trusted-generated>]"

[<EntryPoint>]
let main argv =
    try
        if argv.Length <> 3 && argv.Length <> 5 then
            eprintfn "%s" usage
            2
        else
            let optimizationName = argv[0]
            let optimization = parseOptimization optimizationName
            let runtimeProfile = argv |> Array.skip 3 |> Array.toList |> parseRuntimeProfile
            let outputDirectory = Path.GetFullPath argv[1]
            let sourcePath = Path.GetFullPath argv[2]
            let source = File.ReadAllText sourcePath
            Directory.CreateDirectory outputDirectory |> ignore
            let bodies = compileBodies (Path.GetFileName sourcePath) source
            let artifact =
                OwningStackAot.compileMailboxWithProfile
                    (LlvmToolchain.discover ()) optimization runtimeProfile outputDirectory
                    bodies.Initialize bodies.Begin bodies.Resume
            let compiledRuntimeProfile = runtimeProfileName artifact.RuntimeProfile
            let hasRefinedScalars = not bodies.ScalarDefinitions.IsEmpty
            let unicodeOracle = if hasRefinedScalars then box null else box (interpreterOracle bodies "A🙂" "δ" "\u0000🚀")
            let emptyOracle = if hasRefinedScalars then box null else box (interpreterOracle bodies "" "" "B")
            let errorOracle = if hasRefinedScalars then box null else box (interpreterOracle bodies "Z" "" "ERR")
            let errorRoundTripOracle = if hasRefinedScalars then box null else box (interpreterErrorRoundTrip bodies)
            let sourceDerivedRefinementOracle =
                if hasRefinedScalars then box (refinedStepOracle bodies)
                else null
            let controllerReservedStorageBytes =
                artifact.ControllerReservedStorageBytes
                |> Option.map box
                |> Option.defaultValue null
            let sourceTypeIds =
                bodies.SourceTypeIds
                |> Map.toList
                |> List.filter (fun (name, _) ->
                    name <> "Int" && name <> "Bool" && name <> "Unit")
                |> List.map (fun (name, typeId) -> name, box typeId)
                |> dict
            let entries =
                artifact.Entries
                |> List.map (fun entry ->
                    {| role = entry.Role
                       functionSymbol = entry.FunctionSymbol
                       entryFrameSymbol = entry.EntryFrameSymbol
                       inputTypes = entry.InputTypes |> List.map IrTypes.format
                       outputTypes = entry.OutputTypes |> List.map IrTypes.format
                       inputTypeIndexes = entry.InputTypeIndexes
                       outputTypeIndexes = entry.OutputTypeIndexes
                       diagnosticIds = entry.DiagnosticIds
                       frameSourcePath = entry.FrameSourcePath
                       sourceIrPath = entry.SourceIrPath |})
            let layouts =
                artifact.Layouts
                |> List.mapi (fun layoutIndex layout ->
                    {| layoutIndex = uint32 layoutIndex
                       typeName = layout.TypeName
                       irType = IrTypes.format layout.Type
                       payloadBytes = layout.PayloadBytes
                       extentBytes = layout.ExtentBytes
                       isDynamic = layout.IsDynamic
                       minimumPayloadBytes = layout.MinimumPayloadBytes
                       minimumExtentBytes = layout.MinimumExtentBytes
                       fields =
                         layout.Fields
                         |> List.map (fun field ->
                             {| fieldName = field.FieldName
                                fieldType = IrTypes.format field.FieldType
                                offsetBytes = field.OffsetBytes
                                payloadBytes = field.PayloadBytes
                                extentBytes = field.ExtentBytes
                                isOffsetDynamic = field.IsOffsetDynamic
                                isDynamic = field.IsDynamic
                                minimumPayloadBytes = field.MinimumPayloadBytes
                                minimumExtentBytes = field.MinimumExtentBytes |}) |})
            let diagnostics =
                artifact.Diagnostics
                |> List.map (fun diagnostic ->
                    {| id = diagnostic.Id
                       entryRole = diagnostic.EntryRole |> Option.defaultValue null
                       code = diagnostic.Code
                       message = diagnostic.Message
                       word = diagnostic.Word |> Option.defaultValue null
                       file = diagnostic.File |> Option.defaultValue null
                       line = diagnostic.Line |> Option.map box |> Option.defaultValue null
                       column = diagnostic.Column |> Option.map box |> Option.defaultValue null
                       length = diagnostic.Length |> Option.map box |> Option.defaultValue null
                       expected = diagnostic.Expected
                       actual = diagnostic.Actual |})
            let frameAllocaByRole = artifact.BackendMetadataPerFrameBytes |> Map.toList |> dict
            let callbackMetadataByRole = artifact.CallbackMetadataPerEntryBytes |> Map.toList |> dict
            let maximumBound (values: Map<string, int>) =
                values |> Map.toSeq |> Seq.map snd |> Seq.fold max 0
            let result =
                {| optimization = optimizationName
                   runtimeProfile = compiledRuntimeProfile
                   sourcePath = sourcePath
                   sameVerifiedProgramInstance = true
                   modulePath = artifact.LibraryPath
                   llvmIrPath = artifact.LlvmIrPath
                   metadataSourcePath = artifact.MetadataSourcePath
                   manifestPath = artifact.ManifestPath
                   layouts = layouts
                   entries = entries
                   diagnostics = diagnostics
                   backendBounds = {| perFrameAllocaBytes = maximumBound artifact.BackendMetadataPerFrameBytes
                                      perFrameAllocaBytesByRole = frameAllocaByRole
                                      callbackMetadataPerEntryBytes = maximumBound artifact.CallbackMetadataPerEntryBytes
                                      callbackMetadataPerEntryBytesByRole = callbackMetadataByRole
                                      metadataPeakBoundBytes = artifact.BackendMetadataPeakBoundBytes
                                      layoutScannerScratchBytes = artifact.RuntimeLayoutScannerScratchBytes
                                      preflightSpanTableBytes = artifact.PreflightSpanTableBytes
                                      controllerReservedStorageBytes = controllerReservedStorageBytes |}
                   sourceDerivedTypeIds = {| typeIds = sourceTypeIds |}
                   interpreterOracle = {| unicode = unicodeOracle; empty = emptyOracle; error = errorOracle; errorRoundTrip = errorRoundTripOracle |}
                   sourceDerivedRefinementOracle = sourceDerivedRefinementOracle |}
            Console.WriteLine(JsonSerializer.Serialize(result))
            0
    with error ->
        eprintfn "%s" (error.ToString())
        1
