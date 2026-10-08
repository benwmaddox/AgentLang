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

let private compileBodies (source: string) =
    let document =
        match FlowParser.parseDocumentWithVersion 2 "owning-mailbox.flow" source with
        | Ok parsed -> parsed
        | Error diagnostic -> failwith (Diagnostics.render diagnostic)
    let records = document.Records |> List.map (fun record -> record.Name, record) |> Map.ofList
    let words =
        generatedRecordEntries records
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
          Scalars = Map.empty
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
    let bodies =
        { Program = compiled.Program
          CompilerContext = compiled.Context.CompilerContext
          SourceOrigins = compiled.Context.SourceOrigins
          SourceTypeIds =
            (records
             |> Map.toList
             |> List.mapi (fun index (name, _) -> name, uint32 (index + 4)))
            |> List.append [ "Int", 1u; "Bool", 2u; "Unit", 3u ]
            |> fun values -> values @ [ "String", uint32 (records.Count + 4) ]
            |> Map.ofList
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

let private parseOptimization = function
    | "O0" -> LlvmOptimization.O0
    | "O2" -> LlvmOptimization.O2
    | value -> invalidArg "optimization" $"Unsupported optimization '{value}'; expected O0 or O2."

let private usage = "Usage: AgentLang.OwningMailbox <O0|O2> <output-directory> <owning-mailbox.flow>"

[<EntryPoint>]
let main argv =
    try
        if argv.Length <> 3 then
            eprintfn "%s" usage
            2
        else
            let optimizationName = argv[0]
            let optimization = parseOptimization optimizationName
            let outputDirectory = Path.GetFullPath argv[1]
            let sourcePath = Path.GetFullPath argv[2]
            let source = File.ReadAllText sourcePath
            Directory.CreateDirectory outputDirectory |> ignore
            let bodies = compileBodies source
            let artifact =
                OwningStackAot.compileMailbox
                    (LlvmToolchain.discover ()) optimization outputDirectory
                    bodies.Initialize bodies.Begin bodies.Resume
            let unicodeOracle = interpreterOracle bodies "A🙂" "δ" "\u0000🚀"
            let emptyOracle = interpreterOracle bodies "" "" "B"
            let controllerReservedStorageBytes =
                artifact.ControllerReservedStorageBytes
                |> Option.map box
                |> Option.defaultValue null
            let sourceTypeIds =
                dict [ "Continuation", Map.find "Continuation" bodies.SourceTypeIds
                       "State", Map.find "State" bodies.SourceTypeIds
                       "String", Map.find "String" bodies.SourceTypeIds ]
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
                |> List.map (fun layout ->
                    {| typeName = layout.TypeName
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
                   interpreterOracle = {| unicode = unicodeOracle; empty = emptyOracle |} |}
            Console.WriteLine(JsonSerializer.Serialize(result))
            0
    with error ->
        eprintfn "%s" (error.ToString())
        1
