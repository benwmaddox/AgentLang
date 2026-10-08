module AgentLang.NativeDispatch.Program

open System
open System.IO
open System.Text.Json
open AgentLang
open AgentLang.Llvm

type private HandlerBodies =
    { Program: VerifiedIrProgram
      CompilerContext: Compiler.IrLoweringContext
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
              Documentation = "Generated record operation."
              Body = []
              SourceText = ""
              Span = sourceSpan "<generated-native-dispatch-record>" 1 }
        let entry builtin wordName inputs outputs =
            { Definition = definition wordName inputs outputs
              Builtin = Some builtin
              Status = Persistent
              Maturity = LibraryWord
              Revision = 1 }
        let constructor =
            entry (RecordConstructor name) (prefix + ".new") (record.Fields |> List.map (fun field -> field.Type)) [ TNamed name ]
        let accessors =
            record.Fields
            |> List.map (fun field ->
                entry (RecordAccessor(name, field.Name)) (prefix + "." + field.Name) [ TNamed name ] [ field.Type ])
        constructor :: accessors)

let private compileHandlers (source: string) =
    let document =
        match FlowParser.parseDocumentWithVersion 2 "mailbox.flow" source with
        | Ok parsed -> parsed
        | Error diagnostic -> failwith (Diagnostics.render diagnostic)
    let records = document.Records |> List.map (fun record -> record.Name, record) |> Map.ofList
    let words =
        (generatedRecordEntries records)
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
              RevisionIntent = FlowLowering.FlowWordRevisionIntent.Add(WordId("mailbox-source-" + definition.Name), 1) })
    let compiled = FlowLowering.compileBatchWords context changes
    let entrySpan name = sourceSpan ("<" + name + ">") 1
    let compileEntry entryName target inputTypes =
        Compiler.compileIrBodyAgainstProgramWithSourceOrigins
            compiled.Context.CompilerContext
            compiled.Program
            entryName
            inputTypes
            [ Call(target, entrySpan entryName) ]
            compiled.Context.SourceOrigins
    let handlers =
        { Program = compiled.Program
          CompilerContext = compiled.Context.CompilerContext
          Initialize = compileEntry "mailbox.initialize" "mailbox.initialize" [ TInt ]
          Begin = compileEntry "mailbox.begin" "mailbox.begin" [ TNamed "State"; TInt ]
          Resume = compileEntry "mailbox.resume" "mailbox.resume" [ TNamed "State"; TNamed "Continuation"; TInt ] }
    let bodies = [ handlers.Initialize; handlers.Begin; handlers.Resume ]
    if not (VerifiedIrProgram.isBackendExecutable handlers.Program) then
        invalidOp "Flow lowering did not produce a backend-authorized VerifiedIrProgram."
    if bodies |> List.exists (fun body -> not (Object.ReferenceEquals(VerifiedIrBody.program body, handlers.Program))) then
        invalidOp "Native module entry wrappers do not share one VerifiedIrProgram instance."
    handlers

let private parseOptimization = function
    | "O0" -> LlvmOptimization.O0
    | "O2" -> LlvmOptimization.O2
    | value -> invalidArg "optimization" $"Unsupported optimization '{value}'; expected O0 or O2."

let private usage = "Usage: AgentLang.NativeDispatch <O0|O2> <output-directory> <mailbox.flow>"

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
            let handlers = compileHandlers source
            let entries: NativeModuleEntry list =
                [ { Name = "mailbox.initialize"; Body = handlers.Initialize }
                  { Name = "mailbox.begin"; Body = handlers.Begin }
                  { Name = "mailbox.resume"; Body = handlers.Resume } ]
            let diagnostics = NativeDiagnosticSources.fromLoweringContext handlers.CompilerContext
            let artifact =
                LlvmAot.compileModule
                    (LlvmToolchain.discover ())
                    optimization
                    outputDirectory
                    diagnostics
                    entries
            let result =
                {| optimization = optimizationName
                   sourcePath = sourcePath
                   sameVerifiedProgramInstance = true
                   libraryPath = artifact.LibraryPath
                   manifestPath = artifact.ManifestPath
                   fingerprint = artifact.Fingerprint
                   entryIrPaths = artifact.EntryIrPaths
                   metadataSourcePath = artifact.MetadataSourcePath
                   objectPaths = artifact.ObjectPaths |}
            Console.WriteLine(JsonSerializer.Serialize(result))
            0
    with error ->
        eprintfn "%s" (error.ToString())
        1
