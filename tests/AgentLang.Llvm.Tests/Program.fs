module AgentLang.Llvm.Tests

open System
open System.IO
open System.Runtime.InteropServices
open System.Text.Json
open System.Threading
open AgentLang
open AgentLang.Llvm

[<UnmanagedFunctionPointer(CallingConvention.Cdecl)>]
type private RawExecuteDelegate = delegate of nativeint * nativeint * int32 * nativeint -> unit

[<UnmanagedFunctionPointer(CallingConvention.Cdecl)>]
type private LayoutDelegate = delegate of nativeint -> unit

[<UnmanagedFunctionPointer(CallingConvention.Cdecl)>]
type private CapacityDelegate = delegate of unit -> int32

let mutable private assertions = 0

let private check name condition =
    assertions <- assertions + 1
    if not condition then failwith $"{name}: assertion failed"

let private printStage (name: string) =
    Console.WriteLine("[native-conformance] " + name)
    Console.Out.Flush()

let private span file column =
    { File = file
      Line = 1
      Column = column
      Length = 1 }

let private wordEntry (name: string) (inputs: LangType list) (outputs: LangType list) (effects: Set<string>) (body: Expr list) : WordEntry =
    let sourceSpan = span (name + ".agent") 1
    let definition =
        { Name = name
          Inputs = inputs
          Outputs = outputs
          Effects = effects
          Maturity = LibraryWord
          Revision = 1
          Documentation = "Native LLVM conformance fixture."
          Body = body
          SourceText = name
          Span = sourceSpan }
    { Definition = definition
      Builtin = None
      Status = Persistent
      Maturity = LibraryWord
      Revision = 1 }

let private generatedScalarEntry (name: string) (builtin: Builtin) (inputs: LangType list) (outputs: LangType list) =
    let entry = wordEntry name inputs outputs Set.empty []
    { entry with Builtin = Some builtin }

let private lowerFirst (name: string) =
    if String.IsNullOrEmpty name then name
    else string (Char.ToLowerInvariant name[0]) + name.Substring(1)

let private generatedRecordEntries (records: Map<string, RecordDefinition>) =
    records
    |> Map.toList
    |> List.collect (fun (name, record) ->
        let prefix = lowerFirst name
        let constructorName = prefix + ".new"
        let constructor = wordEntry constructorName (record.Fields |> List.map (fun field -> field.Type)) [ TNamed name ] Set.empty []
        let constructor = { constructor with Builtin = Some(RecordConstructor name) }
        let accessors =
            record.Fields
            |> List.map (fun field ->
                let accessorName = prefix + "." + field.Name
                let accessor = wordEntry accessorName [ TNamed name ] [ field.Type ] Set.empty []
                { accessor with Builtin = Some(RecordAccessor(name, field.Name)) })
        constructor :: accessors)

let private contextWithDefinitions
    (extraWords: WordEntry list)
    (records: Map<string, RecordDefinition>)
    (scalarDefinitions: (ScalarTypeDefinition * string * string) list)
    : Compiler.IrLoweringContext =
    let generatedWords =
        scalarDefinitions
        |> List.collect (fun (scalar, constructorName, accessorName) ->
            [ generatedScalarEntry constructorName (ScalarConstructor scalar.Name) [ scalar.BaseType ] [ TNamed scalar.Name ]
              generatedScalarEntry accessorName (ScalarAccessor scalar.Name) [ TNamed scalar.Name ] [ scalar.BaseType ] ])
    let scalarMap = scalarDefinitions |> List.map (fun (scalar, _, _) -> scalar.Name, scalar) |> Map.ofList
    let words =
        extraWords @ generatedRecordEntries records @ generatedWords
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
      Records = records
      Scalars = scalarMap
      Enums = Map.empty
      WordIds = wordIds }

let private contextWithScalarDefinitions extraWords scalarDefinitions =
    contextWithDefinitions extraWords Map.empty scalarDefinitions

let private scalarDefinition name baseType validator =
    { Name = name
      BaseType = baseType
      Validator = validator
      SourceText = "scalar " + name
      Span = span (name + ".agent") 1 }

let private contextWithScalars (extraWords: WordEntry list) (scalars: ScalarTypeDefinition list) =
    let definitions =
        scalars
        |> List.map (fun scalar -> scalar, scalar.Name + ".make", scalar.Name + ".value")
    contextWithScalarDefinitions extraWords definitions

let private contextWithRecords (extraWords: WordEntry list) (records: RecordDefinition list) =
    let recordMap = records |> List.map (fun record -> record.Name, record) |> Map.ofList
    contextWithDefinitions extraWords recordMap []

let private contextWith (extraWords: WordEntry list) : Compiler.IrLoweringContext =
    contextWithScalarDefinitions extraWords []

let private contextWithEnums (extraWords: WordEntry list) (definitions: EnumDefinition list) : Compiler.IrLoweringContext =
    let enumEntries =
        definitions
        |> List.collect (fun definition ->
            definition.Cases
            |> List.map (fun caseName ->
                let name = definition.Name + "." + caseName
                let constructor = wordEntry name [] [ TNamed definition.Name ] Set.empty []
                { constructor with Builtin = Some(EnumCaseConstructor(definition.Name, caseName)) }))
    let words =
        extraWords @ enumEntries
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
      Records = Map.empty
      Scalars = Map.empty
      Enums = definitions |> List.map (fun definition -> definition.Name, definition) |> Map.ofList
      WordIds = wordIds }

let private compileBody context name expressions =
    let verifiedProgram = Compiler.compileIrProgram context
    Compiler.compileIrBodyAgainstProgram context verifiedProgram name [] expressions

let private noOpHost () : IrInterpreterHost =
    { PreflightEffects = fun _ _ _ -> ()
      ChargeInstruction = fun _ _ -> ()
      RecordBranchOutcome = fun _ _ _ -> ()
      RecordUse = ignore
      InvokeEffect = fun _ -> EffectUnit
      WordDefinitionSpan = fun _ -> None
      PrimitiveDefinitionSpan = fun _ -> None }

let private interpreterResult name body =
    IrInterpreter.executeBody (noOpHost ()) name body

let private interpreterResultWithSources (sources: NativeDiagnosticSources) name body =
    let host =
        { noOpHost () with
            WordDefinitionSpan = fun word -> sources.WordDefinitionSpans.TryFind word
            PrimitiveDefinitionSpan = fun word -> sources.PrimitiveDefinitionSpans.TryFind word }
    IrInterpreter.executeBody host name body

let private interpreterResultAndSteps name body =
    let mutable steps = 0
    let host = { noOpHost () with ChargeInstruction = fun _ _ -> steps <- steps + 1 }
    IrInterpreter.executeBody host name body, steps

let private errorOf action =
    try
        action ()
        failwith "Expected a LanguageException."
    with
    | LanguageException diagnostic -> diagnostic

let private formatValues values =
    values |> List.map Types.formatValue

let private fixtureRoot = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "scalar-cases.json")))
let private recordFixtureRoot = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "record-cases.json")))
let private stateReentryFixtureRoot = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "state-reentry-cases.json")))

let private fixtureValues (name: string) =
    fixtureRoot.RootElement.GetProperty(name).EnumerateArray()
    |> Seq.map (fun value -> value.GetString())
    |> Seq.toList

let private fixtureError (name: string) : JsonElement =
    fixtureRoot.RootElement.GetProperty(name)

let private recordFixtureValues (name: string) =
    recordFixtureRoot.RootElement.GetProperty(name).EnumerateArray()
    |> Seq.map (fun value -> value.GetString())
    |> Seq.toList

let private recordFixtureError (name: string) : JsonElement =
    recordFixtureRoot.RootElement.GetProperty(name)

let private stateReentryDiagnosticFixture (name: string) : JsonElement =
    stateReentryFixtureRoot.RootElement.GetProperty("diagnostics").GetProperty(name)

let private artifactRoot =
    let run = Guid.NewGuid().ToString("N")
    Path.Combine(Directory.GetCurrentDirectory(), ".agentlang", "native-validation", "tests-" + run)

let private compileNative (name: string) (optimization: LlvmOptimization) (body: VerifiedIrBody) =
    let directory = Path.Combine(artifactRoot, name, string optimization)
    LlvmAot.compile (LlvmToolchain.discover()) optimization directory NativeDiagnosticSources.empty body

let private compileNativeWithSources (name: string) (optimization: LlvmOptimization) (sources: NativeDiagnosticSources) (body: VerifiedIrBody) =
    let directory = Path.Combine(artifactRoot, name, string optimization)
    LlvmAot.compile (LlvmToolchain.discover()) optimization directory sources body

let private compareSuccessfulCase (fixtureName: string) (executionName: string) (body: VerifiedIrBody) (expected: Value list) =
    check (fixtureName + " independent expected result") (formatValues expected = fixtureValues fixtureName)
    let interpreted, expectedSteps = interpreterResultAndSteps executionName body
    check (fixtureName + " interpreter agrees with independent expected result") (interpreted = expected)
    for optimization in [ LlvmOptimization.O0; LlvmOptimization.O2 ] do
        use native = compileNative fixtureName optimization body
        let actual = native.Execute executionName
        check ($"{fixtureName} {optimization} native/interpreter parity") (actual.Values = interpreted)
        check ($"{fixtureName} {optimization} step count") (actual.StepsConsumed = expectedSteps)

let private compareSuccessfulRecordCase (fixtureName: string) (executionName: string) (body: VerifiedIrBody) (expected: Value list) =
    check (fixtureName + " independent expected result") (formatValues expected = recordFixtureValues fixtureName)
    let interpreted, expectedSteps = interpreterResultAndSteps executionName body
    check (fixtureName + " interpreter agrees with independent expected result") (interpreted = expected)
    for optimization in [ LlvmOptimization.O0; LlvmOptimization.O2 ] do
        use native = compileNative fixtureName optimization body
        let ordinary = native.Execute executionName
        check ($"{fixtureName} {optimization} existing Execute API decodes records") (ordinary.Values = interpreted && ordinary.StepsConsumed = expectedSteps)
        use retained = native.ExecuteRetained executionName
        check ($"{fixtureName} {optimization} retained result decodes records") (retained.Decode() = interpreted && retained.Values = interpreted)
        check ($"{fixtureName} {optimization} retained result preserves instruction fuel") (retained.StepsConsumed = expectedSteps)

let private compareErrorCase (fixtureName: string) (executionName: string) (sources: NativeDiagnosticSources) (body: VerifiedIrBody) =
    let expectedFixture = fixtureError fixtureName
    let interpreted = errorOf (fun () -> interpreterResultWithSources sources executionName body |> ignore)
    check (fixtureName + " interpreter code fixture") (interpreted.Code = expectedFixture.GetProperty("code").GetString())
    check (fixtureName + " interpreter word fixture") (interpreted.Word = Some(expectedFixture.GetProperty("word").GetString()))
    let expectedActual =
        expectedFixture.GetProperty("actual").EnumerateArray()
        |> Seq.map (fun value -> value.GetString())
        |> Seq.toList
    check (fixtureName + " interpreter actual fixture") (interpreted.Actual = expectedActual)
    let mutable expectedMessage = Unchecked.defaultof<JsonElement>
    if expectedFixture.TryGetProperty("message", &expectedMessage) then
        check (fixtureName + " interpreter message fixture") (interpreted.Message = expectedMessage.GetString())
    let mutable expectedList = Unchecked.defaultof<JsonElement>
    if expectedFixture.TryGetProperty("expected", &expectedList) then
        let values = expectedList.EnumerateArray() |> Seq.map (fun value -> value.GetString()) |> Seq.toList
        check (fixtureName + " interpreter expected fixture") (interpreted.Expected = values)
    let expectedSpan =
        let spanFixture = expectedFixture.GetProperty("span")
        if spanFixture.ValueKind = JsonValueKind.Null then None
        else
            Some
                { File = spanFixture.GetProperty("file").GetString()
                  Line = spanFixture.GetProperty("line").GetInt32()
                  Column = spanFixture.GetProperty("column").GetInt32()
                  Length = spanFixture.GetProperty("length").GetInt32() }
    check (fixtureName + " interpreter definition-span fixture") (interpreted.Span = expectedSpan)
    for optimization in [ LlvmOptimization.O0; LlvmOptimization.O2 ] do
        use native = compileNativeWithSources fixtureName optimization sources body
        let actual = errorOf (fun () -> native.Execute executionName |> ignore)
        check ($"{fixtureName} {optimization} complete diagnostic parity") (actual = interpreted)

let private rawExecute (libraryPath: string) =
    let handle = NativeLibrary.Load libraryPath
    let pointer = NativeLibrary.GetExport(handle, "agentlang_execute")
    Marshal.GetDelegateForFunctionPointer<RawExecuteDelegate>(pointer), handle

let private allocateRawArena byteCapacity nodeCapacity generation =
    let data = Marshal.AllocHGlobal(max 8 byteCapacity)
    let nodes = Marshal.AllocHGlobal(max NativeAbi.NodeSize (nodeCapacity * NativeAbi.NodeSize))
    let descriptor = Marshal.AllocHGlobal NativeAbi.ArenaSize
    Marshal.WriteIntPtr(descriptor, NativeAbi.ArenaDataOffset, data)
    Marshal.WriteInt32(descriptor, NativeAbi.ArenaByteCapacityOffset, byteCapacity)
    Marshal.WriteInt32(descriptor, NativeAbi.ArenaUsedOffset, 0)
    Marshal.WriteIntPtr(descriptor, NativeAbi.ArenaNodesOffset, nodes)
    Marshal.WriteInt32(descriptor, NativeAbi.ArenaNodeCapacityOffset, nodeCapacity)
    Marshal.WriteInt32(descriptor, NativeAbi.ArenaNodeCountOffset, 0)
    Marshal.WriteInt32(descriptor, NativeAbi.ArenaGenerationOffset, generation)
    Marshal.WriteInt32(descriptor, NativeAbi.ArenaFlagsOffset, 0)
    Marshal.WriteInt64(descriptor, NativeAbi.ArenaReservedOffset, 0L)
    descriptor, data, nodes

let private freeRawArena (descriptor, data, nodes) =
    Marshal.FreeHGlobal descriptor
    Marshal.FreeHGlobal data
    Marshal.FreeHGlobal nodes

let private withRawExecutionBuffers
    (native: NativeCompiledProgram)
    scratchByteCapacity
    scratchNodeCapacity
    retainedByteCapacity
    retainedNodeCapacity
    (action: nativeint -> nativeint -> nativeint -> nativeint -> nativeint -> unit) =
    let scratch = allocateRawArena scratchByteCapacity scratchNodeCapacity 7
    let retained = allocateRawArena retainedByteCapacity retainedNodeCapacity 11
    let context = Marshal.AllocHGlobal NativeAbi.ContextSize
    // Raw guard tests reserve one unadvertised slot for an overrun canary.
    let outputSlotCapacity = max 1 native.OutputCount + 1
    let outputs = Marshal.AllocHGlobal(outputSlotCapacity * NativeAbi.SlotSize)
    let workspace = Marshal.AllocHGlobal(max NativeAbi.SlotSize (native.WorkspaceCapacity * NativeAbi.SlotSize))
    let status = Marshal.AllocHGlobal sizeof<int32>
    try
        Marshal.Copy(Array.zeroCreate<byte> NativeAbi.ContextSize, 0, context, NativeAbi.ContextSize)
        Marshal.WriteInt32(context, NativeAbi.ContextAbiVersionOffset, int NativeAbi.Version)
        Marshal.WriteInt32(context, NativeAbi.ContextErrorMetadataIdOffset, -1)
        Marshal.WriteIntPtr(context, NativeAbi.ContextScratchOffset, nativeint (let descriptor, _, _ = scratch in descriptor))
        Marshal.WriteIntPtr(context, NativeAbi.ContextRetainedOffset, nativeint (let descriptor, _, _ = retained in descriptor))
        Marshal.WriteIntPtr(context, NativeAbi.ContextWorkspaceOffset, nativeint workspace)
        Marshal.WriteInt32(context, NativeAbi.ContextWorkspaceCapacityOffset, native.WorkspaceCapacity)
        Marshal.WriteInt32(status, NativeAbi.StatusInvalidRequest)
        action (nativeint context) (nativeint outputs) (nativeint status) (nativeint (let descriptor, _, _ = scratch in descriptor)) (nativeint (let descriptor, _, _ = retained in descriptor))
    finally
        Marshal.FreeHGlobal status
        Marshal.FreeHGlobal workspace
        Marshal.FreeHGlobal outputs
        Marshal.FreeHGlobal context
        freeRawArena retained
        freeRawArena scratch

let private rawExecutionStatusAndSteps (native: NativeCompiledProgram) =
    let execute, library = rawExecute native.LibraryPath
    try
        let mutable statusValue = NativeAbi.StatusInvalidRequest
        let mutable steps = 0
        withRawExecutionBuffers native 1_000_000 10_000 1_000_000 10_000 (fun context outputs status scratch retained ->
            execute.Invoke(context, outputs, native.OutputCount, status)
            statusValue <- Marshal.ReadInt32 status
            steps <- Marshal.ReadInt32(context, NativeAbi.ContextStepsConsumedOffset))
        statusValue, steps
    finally
        NativeLibrary.Free library

let private testLayoutAndRequestGuards multiOutputBody scratchBody =
    use native = compileNative "abi-guard" LlvmOptimization.O0 multiOutputBody
    let layoutHandle = NativeLibrary.Load native.LibraryPath
    try
        let capacityQuery = Marshal.GetDelegateForFunctionPointer<CapacityDelegate>(NativeLibrary.GetExport(layoutHandle, "agentlang_output_capacity"))
        check "exported workspace capacity query matches managed API" (capacityQuery.Invoke() = native.WorkspaceCapacity)
        check "logical result count and public capacity stay distinct from workspace capacity" (
            native.OutputCount = 4 && native.OutputCapacity = 4 && native.WorkspaceCapacity >= native.OutputCapacity)
        let layout = Marshal.GetDelegateForFunctionPointer<LayoutDelegate>(NativeLibrary.GetExport(layoutHandle, "agentlang_abi_layout"))
        let layoutBuffer = Marshal.AllocHGlobal(18 * sizeof<int64>)
        try
            layout.Invoke(nativeint layoutBuffer)
            let actualLayout = [ for index in 0 .. 17 -> Marshal.ReadInt64(layoutBuffer, index * sizeof<int64>) ]
            let expectedOffsets =
                [ NativeAbi.ContextAbiVersionOffset
                  NativeAbi.ContextStepsConsumedOffset
                  NativeAbi.ContextErrorMetadataIdOffset
                  NativeAbi.ContextReservedOffset
                  NativeAbi.ContextErrorArgument0Offset
                  NativeAbi.ContextErrorArgument1Offset
                  NativeAbi.ContextScratchOffset
                  NativeAbi.ContextRetainedOffset
                  NativeAbi.ContextWorkspaceOffset
                  NativeAbi.ContextWorkspaceCapacityOffset
                  NativeAbi.ContextReservedTailOffset
                  NativeAbi.ContextInputOwnerOffset
                  NativeAbi.ContextInputRootsOffset
                  NativeAbi.ContextInputRootTypeIdsOffset
                  NativeAbi.ContextInputRootCountOffset
                  NativeAbi.ContextReservedV3Offset ]
            let expectedLayout =
                [ int64 NativeAbi.ContextSize
                  int64 NativeAbi.ContextAlignment ]
                @ (expectedOffsets |> List.map int64)
            check "LLVM-reported context size/alignment/field offsets" (actualLayout = expectedLayout)
            use v2Json = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "abi-v2.json")))
            let v2 = v2Json.RootElement
            use v3Json = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "abi-v3.json")))
            let v3 = v3Json.RootElement
            let v2ContextFixture = v2.GetProperty("context")
            let v3ContextFixture = v3.GetProperty("context")
            check "historical ABI v2 fixture preserves its independent context size and alignment" (
                v2.GetProperty("abiVersion").GetInt32() = 2
                && v2.GetProperty("nativeLayoutOracle").GetString().Contains("runtime_test.c --layout-json")
                && v2ContextFixture.GetProperty("size").GetInt32() = 64
                && v2ContextFixture.GetProperty("alignment").GetInt32() = 8
                && (v2ContextFixture.GetProperty("fields").EnumerateArray() |> Seq.map (fun field -> field.GetProperty("offset").GetInt32()) |> Seq.toList)
                    = [ 0; 4; 8; 12; 16; 24; 32; 40; 48; 56; 60 ])
            let v3Fields = v3ContextFixture.GetProperty("fields").EnumerateArray() |> Seq.toList
            check "ABI v3 fixture independently fixes all context offsets and layout export values" (
                v3.GetProperty("abiVersion").GetInt32() = 3
                && v3.GetProperty("nativeLayoutOracle").GetString().Contains("runtime_test.c --layout-json")
                && v3ContextFixture.GetProperty("size").GetInt32() = 96
                && v3ContextFixture.GetProperty("alignment").GetInt32() = 8
                && (v3Fields |> List.map (fun field -> field.GetProperty("offset").GetInt32()))
                    = [ 0; 4; 8; 12; 16; 24; 32; 40; 48; 56; 60; 64; 72; 80; 88; 92 ]
                && (v3ContextFixture.GetProperty("layoutExport").EnumerateArray() |> Seq.map (fun value -> value.GetInt64()) |> Seq.toList)
                    = [ 96L; 8L; 0L; 4L; 8L; 12L; 16L; 24L; 32L; 40L; 48L; 56L; 60L; 64L; 72L; 80L; 88L; 92L ]
                && actualLayout = [ 96L; 8L; 0L; 4L; 8L; 12L; 16L; 24L; 32L; 40L; 48L; 56L; 60L; 64L; 72L; 80L; 88L; 92L ])
            let v3InputContract = v3.GetProperty("inputContract")
            check "ABI v3 fixture describes readonly owner and ordered resolved input slots" (
                v3InputContract.GetProperty("inputOwner").GetString().Contains("read-only")
                && v3InputContract.GetProperty("inputRoots").GetString().Contains("ordered invocation values")
                && v3InputContract.GetProperty("inputRootTypeIds").GetString().Contains("zero-based program type IDs")
                && v3InputContract.GetProperty("import").GetString().Contains("entire input owner's immutable graph"))
            let entryParameters = v2.GetProperty("publicEntry").GetProperty("parameters").EnumerateArray() |> Seq.toList
            let outputCapacityParameter = entryParameters |> List.find (fun parameter -> parameter.GetProperty("name").GetString() = "outputCapacity")
            check "ABI v2 documents signed 32-bit public and workspace capacity signatures" (
                outputCapacityParameter.GetProperty("type").GetString() = "int32_t"
                && v2.GetProperty("workspaceCapacityQuery").GetProperty("return").GetString() = "int32_t")
            let statusPrecondition =
                v2.GetProperty("preconditions").EnumerateArray()
                |> Seq.exists (fun condition -> condition.GetString().Contains("raw callers initialize status to INVALID_REQUEST"))
            check "ABI v2 documents caller status initialization and no-write early rejection" statusPrecondition
            let contextFieldNames = [ "AbiVersion"; "StepsConsumed"; "ErrorMetadataId"; "ReservedPrefix"; "ErrorArgument0"; "ErrorArgument1"; "Scratch"; "Retained"; "Workspace"; "WorkspaceCapacity"; "ReservedTail" ]
            let managedV2PrefixOffsets = contextFieldNames |> List.map (fun name -> Marshal.OffsetOf<NativeExecutionContext>(name).ToInt32())
            check "managed ABI v2 prefix offsets remain fixed under v3" (
                managedV2PrefixOffsets = [ 0; 4; 8; 12; 16; 24; 32; 40; 48; 56; 60 ])
            let v3ContextFieldNames = [ "AbiVersion"; "StepsConsumed"; "ErrorMetadataId"; "ReservedPrefix"; "ErrorArgument0"; "ErrorArgument1"; "Scratch"; "Retained"; "Workspace"; "WorkspaceCapacity"; "ReservedTail"; "InputOwner"; "InputRoots"; "InputRootTypeIds"; "InputRootCount"; "ReservedV3" ]
            let managedV3ContextOffsets = v3ContextFieldNames |> List.map (fun name -> Marshal.OffsetOf<NativeExecutionContext>(name).ToInt32())
            let v3ContextOffsets = v3Fields |> List.map (fun field -> field.GetProperty("offset").GetInt32())
            check "managed context size and every ABI v3 field offset match the independent fixture" (
                Marshal.SizeOf<NativeExecutionContext>() = 96
                && v3ContextOffsets = [ 0; 4; 8; 12; 16; 24; 32; 40; 48; 56; 60; 64; 72; 80; 88; 92 ]
                && managedV3ContextOffsets = v3ContextOffsets)
            check "native ABI v3 context layout export matches the independent numeric oracle" (
                actualLayout = (v3ContextFixture.GetProperty("layoutExport").EnumerateArray() |> Seq.map (fun value -> value.GetInt64()) |> Seq.toList))

            let fieldsOf (parent: JsonElement) (name: string) =
                parent.GetProperty(name).GetProperty("fields").EnumerateArray() |> Seq.toList
            check "arena descriptor managed size and alignment match the independent fixture" (
                Marshal.SizeOf<NativeArenaDescriptor>() = 48
                && v2.GetProperty("arena").GetProperty("size").GetInt32() = 48
                && v2.GetProperty("arena").GetProperty("alignment").GetInt32() = 8)
            let arenaFields = fieldsOf v2 "arena"
            let arenaOffsets = [ "Data"; "ByteCapacity"; "Used"; "Nodes"; "NodeCapacity"; "NodeCount"; "Generation"; "Flags"; "Reserved" ] |> List.map (fun name -> Marshal.OffsetOf<NativeArenaDescriptor>(name).ToInt32())
            check "arena descriptor managed offsets match the independent fixture" (arenaOffsets = (arenaFields |> List.map (fun field -> field.GetProperty("offset").GetInt32())) && arenaOffsets = [ 0; 8; 12; 16; 24; 28; 32; 36; 40 ])
            let nodeFields = fieldsOf v2 "node"
            let nodeOffsets = [ "TypeId"; "FieldCount"; "PayloadOffset"; "PayloadBytes"; "Mark"; "Reserved"; "ForwardHandle" ] |> List.map (fun name -> Marshal.OffsetOf<NativeNode>(name).ToInt32())
            check "node managed size, alignment, and offsets match the independent fixture" (
                Marshal.SizeOf<NativeNode>() = 32
                && v2.GetProperty("node").GetProperty("size").GetInt32() = 32
                && v2.GetProperty("node").GetProperty("alignment").GetInt32() = 8
                && nodeOffsets = (nodeFields |> List.map (fun field -> field.GetProperty("offset").GetInt32()))
                && nodeOffsets = [ 0; 4; 8; 12; 16; 20; 24 ])
            let typeFields = fieldsOf v2 "typeDescriptor"
            let typeOffsets = [ "Kind"; "FieldCount"; "FieldTypes" ] |> List.map (fun name -> Marshal.OffsetOf<NativeTypeDescriptor>(name).ToInt32())
            check "type descriptor managed size, alignment, and offsets match the independent fixture" (
                Marshal.SizeOf<NativeTypeDescriptor>() = 16
                && v2.GetProperty("typeDescriptor").GetProperty("size").GetInt32() = 16
                && v2.GetProperty("typeDescriptor").GetProperty("alignment").GetInt32() = 8
                && typeOffsets = (typeFields |> List.map (fun field -> field.GetProperty("offset").GetInt32()))
                && typeOffsets = [ 0; 4; 8 ])
            let programFields = fieldsOf v2 "programDescriptor"
            let programOffsets = [ "Types"; "TypeCount"; "Reserved" ] |> List.map (fun name -> Marshal.OffsetOf<NativeProgramDescriptor>(name).ToInt32())
            check "program descriptor managed size, alignment, and offsets match the independent fixture" (
                Marshal.SizeOf<NativeProgramDescriptor>() = 16
                && v2.GetProperty("programDescriptor").GetProperty("size").GetInt32() = 16
                && v2.GetProperty("programDescriptor").GetProperty("alignment").GetInt32() = 8
                && programOffsets = (programFields |> List.map (fun field -> field.GetProperty("offset").GetInt32()))
                && programOffsets = [ 0; 8; 12 ])

            let typeIndices = v2.GetProperty("typeIndices")
            let typeKinds = v2.GetProperty("typeKinds")
            check "ABI v2 fixture separates type array indices from descriptor kind tags" (
                typeIndices.GetProperty("int").GetUInt32() = NativeAbi.TypeIdInt
                && typeIndices.GetProperty("bool").GetUInt32() = NativeAbi.TypeIdBool
                && typeIndices.GetProperty("unit").GetUInt32() = NativeAbi.TypeIdUnit
                && typeKinds.GetProperty("int").GetUInt32() = NativeAbi.TypeKindInt
                && typeKinds.GetProperty("bool").GetUInt32() = NativeAbi.TypeKindBool
                && typeKinds.GetProperty("unit").GetUInt32() = NativeAbi.TypeKindUnit
                && typeKinds.GetProperty("record").GetUInt32() = NativeAbi.TypeKindRecord)
            let v2Example = v2.GetProperty("resultBuffer").GetProperty("example")
            check "ABI v2 fixture separates public output slots from workspace slots" (
                v2Example.GetProperty("logicalOutputCount").GetInt32() = 1
                && v2Example.GetProperty("publicOutputCapacity").GetInt32() = 1
                && v2Example.GetProperty("maxRecordConstructorFieldCount").GetInt32() = 3
                && v2Example.GetProperty("requiredWorkspaceCapacity").GetInt32() = 4
                && v2.GetProperty("workspaceCapacityQuery").GetProperty("meaning").GetString().Contains("not the public root capacity"))
            check "runtime status values match the independent fixture" (
                let status = v2.GetProperty("status")
                status.GetProperty("success").GetInt32() = NativeAbi.StatusSuccess
                && status.GetProperty("languageDiagnostic").GetInt32() = NativeAbi.StatusDiagnostic
                && status.GetProperty("invalidRequest").GetInt32() = NativeAbi.StatusInvalidRequest
                && status.GetProperty("scratchCapacity").GetInt32() = NativeAbi.StatusScratchCapacity
                && status.GetProperty("retainedCapacity").GetInt32() = NativeAbi.StatusRetainedCapacity
                && status.GetProperty("invalidReference").GetInt32() = NativeAbi.StatusInvalidReference)

            use v1Json = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "abi-v1.json")))
            let v1 = v1Json.RootElement
            let v1Context = v1.GetProperty("context")
            let v1Offsets = v1Context.GetProperty("fields").EnumerateArray() |> Seq.map (fun field -> field.GetProperty("offset").GetInt32()) |> Seq.toList
            check "historical ABI v1 fixture remains unchanged" (
                v1.GetProperty("abiVersion").GetInt32() = 1
                && v1Context.GetProperty("size").GetInt32() = 32
                && v1Context.GetProperty("alignment").GetInt32() = 8
                && v1Offsets = [ 0; 4; 8; 12; 16; 24 ]
                && v1.GetProperty("resultBuffer").GetProperty("example").GetProperty("requiredScratchCapacity").GetInt32() = 2)
        finally
            Marshal.FreeHGlobal layoutBuffer
    finally
        NativeLibrary.Free layoutHandle

    let checkRequestGuards name body =
        use guarded = compileNative name LlvmOptimization.O0 body
        check (name + " native workspace-capacity query") (
            let library = NativeLibrary.Load guarded.LibraryPath
            try
                let query = Marshal.GetDelegateForFunctionPointer<CapacityDelegate>(NativeLibrary.GetExport(library, "agentlang_output_capacity"))
                query.Invoke() = guarded.WorkspaceCapacity
            finally
                NativeLibrary.Free library)
        if name = "abi-scratch-guard" then
            check "one logical result can require two workspace slots" (
                guarded.OutputCount = 1 && guarded.OutputCapacity = 1 && guarded.WorkspaceCapacity = 2)
        let execute, executeHandle = rawExecute guarded.LibraryPath
        try
            let canaryCount = max 1 guarded.OutputCount + 1
            let sentinel = 0x123456789ABCDEFL
            let callGuard label version capacity initialStatus expectedStatus =
                withRawExecutionBuffers guarded 1024 16 1024 16 (fun context outputs status _scratch _retained ->
                    Marshal.WriteInt32(context, NativeAbi.ContextAbiVersionOffset, version)
                    Marshal.WriteInt32(context, NativeAbi.ContextStepsConsumedOffset, 55)
                    Marshal.WriteInt32(context, NativeAbi.ContextErrorMetadataIdOffset, 77)
                    for index in 0 .. canaryCount - 1 do
                        Marshal.WriteInt64(outputs, index * NativeAbi.SlotSize, sentinel + int64 index)
                    Marshal.WriteInt32(status, initialStatus)
                    execute.Invoke(context, outputs, capacity, status)
                    let unchanged =
                        [ 0 .. canaryCount - 1 ]
                        |> List.forall (fun index -> Marshal.ReadInt64(outputs, index * NativeAbi.SlotSize) = sentinel + int64 index)
                    check (name + " " + label + " preserves status and output guards") (
                        Marshal.ReadInt32(status) = expectedStatus && unchanged)
                    check (name + " " + label + " leaves the execution context untouched") (
                        Marshal.ReadInt32(context, NativeAbi.ContextStepsConsumedOffset) = 55
                        && Marshal.ReadInt32(context, NativeAbi.ContextErrorMetadataIdOffset) = 77))
            let statusSentinel = 0x13572468
            callGuard "wrong ABI version" (int NativeAbi.Version + 1) guarded.OutputCount statusSentinel statusSentinel
            withRawExecutionBuffers guarded 1024 16 1024 16 (fun context outputs status _scratch _retained ->
                let sentinel = 0x5A6B7C8D9EAF1021L
                Marshal.WriteInt32(context, NativeAbi.ContextAbiVersionOffset, 2)
                Marshal.WriteIntPtr(context, NativeAbi.ContextInputOwnerOffset, nativeint 1)
                Marshal.WriteIntPtr(context, NativeAbi.ContextInputRootsOffset, nativeint 2)
                Marshal.WriteIntPtr(context, NativeAbi.ContextInputRootTypeIdsOffset, nativeint 3)
                Marshal.WriteInt32(context, NativeAbi.ContextInputRootCountOffset, 4)
                Marshal.WriteInt32(context, NativeAbi.ContextReservedV3Offset, 5)
                for index in 0 .. canaryCount - 1 do
                    Marshal.WriteInt64(outputs, index * NativeAbi.SlotSize, sentinel + int64 index)
                Marshal.WriteInt32(status, statusSentinel)
                execute.Invoke(context, outputs, guarded.OutputCount, status)
                let outputsUnchanged =
                    [ 0 .. canaryCount - 1 ]
                    |> List.forall (fun index -> Marshal.ReadInt64(outputs, index * NativeAbi.SlotSize) = sentinel + int64 index)
                check (name + " ABI v2 prefix is rejected before reading poisoned ABI v3 tail") (
                    Marshal.ReadInt32(status) = statusSentinel
                    && outputsUnchanged
                    && Marshal.ReadInt32(context, NativeAbi.ContextInputRootCountOffset) = 4
                    && Marshal.ReadInt32(context, NativeAbi.ContextReservedV3Offset) = 5))
            callGuard "insufficient public output capacity" (int NativeAbi.Version) (guarded.OutputCount - 1) NativeAbi.StatusInvalidRequest NativeAbi.StatusInvalidRequest
        finally
            NativeLibrary.Free executeHandle

    use legacyNative = compileNative "abi-v1-prefix-guard" LlvmOptimization.O0 multiOutputBody
    let executeLegacy, legacyHandle = rawExecute legacyNative.LibraryPath
    try
        let prefix = Marshal.AllocHGlobal NativeAbi.ContextSize
        let outputCount = max 1 legacyNative.OutputCount
        let outputs = Marshal.AllocHGlobal((outputCount + 1) * NativeAbi.SlotSize)
        let status = Marshal.AllocHGlobal sizeof<int32>
        try
            let sentinel = 0x76543210FEDCBA98L
            Marshal.WriteInt32(prefix, 0, 1)
            Marshal.WriteInt32(prefix, 4, 91)
            Marshal.WriteInt32(prefix, 8, 37)
            Marshal.WriteInt32(prefix, 12, 0)
            Marshal.WriteInt64(prefix, 16, 0x1122334455667788L)
            Marshal.WriteInt64(prefix, 24, 0x2233445566778899L)
            Marshal.WriteIntPtr(prefix, NativeAbi.ContextScratchOffset, nativeint 1)
            Marshal.WriteIntPtr(prefix, NativeAbi.ContextRetainedOffset, nativeint 2)
            Marshal.WriteIntPtr(prefix, NativeAbi.ContextWorkspaceOffset, nativeint 3)
            Marshal.WriteInt32(prefix, NativeAbi.ContextWorkspaceCapacityOffset, 7)
            for index in 0 .. outputCount do Marshal.WriteInt64(outputs, index * NativeAbi.SlotSize, sentinel + int64 index)
            let statusSentinel = 0x13572468
            Marshal.WriteInt32(status, statusSentinel)
            executeLegacy.Invoke(nativeint prefix, nativeint outputs, legacyNative.OutputCount, nativeint status)
            let unchanged = [ 0 .. outputCount ] |> List.forall (fun index -> Marshal.ReadInt64(outputs, index * NativeAbi.SlotSize) = sentinel + int64 index)
            check "ABI v1 prefix is rejected before status or output writes" (Marshal.ReadInt32(status) = statusSentinel && unchanged)
            check "ABI v1 prefix is rejected before context initialization" (
                Marshal.ReadInt32(prefix, 4) = 91
                && Marshal.ReadInt32(prefix, 8) = 37
                && Marshal.ReadInt64(prefix, 16) = 0x1122334455667788L
                && Marshal.ReadInt64(prefix, 24) = 0x2233445566778899L)
        finally
            Marshal.FreeHGlobal status
            Marshal.FreeHGlobal outputs
            Marshal.FreeHGlobal prefix
    finally
        NativeLibrary.Free legacyHandle

    checkRequestGuards "abi-multi-output-guard" multiOutputBody
    checkRequestGuards "abi-scratch-guard" scratchBody

let private testFuelAndSourceMetadata () =
    let callSpan = span "fuel.agent" 9
    let expressions =
        [ 1 .. 5001 ]
        |> List.collect (fun _ -> [ Push(LInt 1L, callSpan); Call("drop", callSpan) ])
    let body = compileBody (contextWith []) "fuel-body" expressions
    let mutable charged = 0
    let host =
        { noOpHost () with ChargeInstruction = fun _ _ -> charged <- charged + 1 }
    let interpreted = errorOf (fun () -> IrInterpreter.executeBody host "fuel-execution" body |> ignore)
    check "interpreter charges the first over-limit instruction" (charged = 10001)
    check "fuel diagnostic retains entry execution name and source span" (
        interpreted.Code = "RUNTIME_STEP_LIMIT"
        && interpreted.Word = Some "fuel-execution"
        && interpreted.Span = Some callSpan)
    for optimization in [ LlvmOptimization.O0; LlvmOptimization.O2 ] do
        use native = compileNative "fuel-source" optimization body
        let actual = errorOf (fun () -> native.Execute "fuel-execution" |> ignore)
        check ($"{optimization} native fuel/source diagnostic matches interpreter") (actual = interpreted)

let private testCallDepth () =
    let callSpan index = span ($"deep-{index}.agent") 5
    let deepWords =
        [ 0 .. 65 ]
        |> List.map (fun index ->
            let name = $"deep.{index}"
            let body = if index = 65 then [] else [ Call($"deep.{index + 1}", callSpan index) ]
            wordEntry name [] [] Set.empty body)
    let context = contextWith deepWords
    let body = compileBody context "deep-body" [ Call("deep.0", span "deep-entry.agent" 1) ]
    let sources = NativeDiagnosticSources.fromLoweringContext context
    let interpreted = errorOf (fun () -> interpreterResultWithSources sources "deep-entry" body |> ignore)
    check "interpreter depth boundary and definition span" (
        interpreted.Code = "RUNTIME_CALL_DEPTH"
        && interpreted.Word = Some "deep.65"
        && interpreted.Span = Some(span "deep.65.agent" 1))
    for optimization in [ LlvmOptimization.O0; LlvmOptimization.O2 ] do
        use native = compileNativeWithSources "call-depth" optimization sources body
        let actual = errorOf (fun () -> native.Execute "deep-entry" |> ignore)
        check ($"{optimization} native call-depth diagnostic matches interpreter") (actual = interpreted)

let private testLargeStackFrame () =
    let outputCount = 1200
    let expressions =
        [ 1 .. outputCount ]
        |> List.collect (fun index ->
            let source = span "large-frame.agent" index
            [ Push(LInt (int64 index), source)
              Push(LInt 1L, source)
              Call("add", source) ])
    let body = compileBody (contextWith []) "large-frame" expressions
    let expected = [ 1 .. outputCount ] |> List.map (fun index -> IntValue(int64 index + 1L))
    let interpreted, expectedSteps = interpreterResultAndSteps "large-frame" body
    check "large-frame independent expected results" (interpreted = expected)
    for optimization in [ LlvmOptimization.O0; LlvmOptimization.O2 ] do
        use native = compileNative "large-frame" optimization body
        let actual = native.Execute "large-frame"
        check ($"large-frame {optimization} values match interpreter") (actual.Values = interpreted)
        check ($"large-frame {optimization} preserves fuel accounting") (actual.StepsConsumed = expectedSteps)

let private testNominalScalarSupport () =
    let support = fixtureRoot.RootElement.GetProperty("native-support")
    let supportedBases = support.GetProperty("nominalScalarBases").EnumerateArray() |> Seq.map (fun value -> value.GetString()) |> Seq.toList
    let excluded = support.GetProperty("excluded").EnumerateArray() |> Seq.map (fun value -> value.GetString()) |> Seq.toList
    let supportedKinds = support.GetProperty("supportedKinds").EnumerateArray() |> Seq.map (fun value -> value.GetString()) |> Seq.toList
    check "scalar fixture records the native nominal support boundary" (
        supportedBases = [ "Int"; "Bool" ]
        && supportedKinds = [ "records" ]
        && excluded = [ "Float"; "String"; "containers"; "effects" ])

    let positiveValidator =
        wordEntry "is-positive?" [ TInt ] [ TBool ] Set.empty [
            Push(LInt 0L, span "is-positive.agent" 1)
            Call("int.greater-than", span "is-positive.agent" 2)
        ]
    let identityBoolValidator =
        wordEntry "identity-bool?" [ TBool ] [ TBool ] Set.empty [
            Call("bool.not", span "identity-bool.agent" 1)
            Call("bool.not", span "identity-bool.agent" 2)
        ]
    let intContext =
        contextWithScalarDefinitions [ positiveValidator ] [
            scalarDefinition "OrderId" TInt None, "OrderId.create", "OrderId.raw"
            scalarDefinition "PositiveId" TInt (Some "is-positive?"), "PositiveId.construct", "PositiveId.unwrap"
        ]
    let intBody = compileBody intContext "nominal-int-cases" [
        Push(LInt 17L, span "nominal-int.agent" 1)
        Call("OrderId.create", span "nominal-int.agent" 2)
        Call("dup", span "nominal-int.agent" 3)
        Call("OrderId.raw", span "nominal-int.agent" 4)
        Push(LInt 5L, span "nominal-int.agent" 5)
        Call("PositiveId.construct", span "nominal-int.agent" 6)
        Call("dup", span "nominal-int.agent" 7)
        Call("PositiveId.unwrap", span "nominal-int.agent" 8)
    ]
    compareSuccessfulCase "nominal-int-cases" "nominal-int-cases" intBody [
        NamedValue("OrderId", IntValue 17L); IntValue 17L
        NamedValue("PositiveId", IntValue 5L); IntValue 5L
    ]

    let boolContext =
        contextWithScalarDefinitions [ identityBoolValidator ] [
            scalarDefinition "Permission" TBool None, "Permission.create", "Permission.raw"
            scalarDefinition "CheckedFlag" TBool (Some "identity-bool?"), "CheckedFlag.construct", "CheckedFlag.unwrap"
        ]
    let boolBody = compileBody boolContext "nominal-bool-cases" [
        Push(LBool true, span "nominal-bool.agent" 1)
        Call("Permission.create", span "nominal-bool.agent" 2)
        Call("dup", span "nominal-bool.agent" 3)
        Call("Permission.raw", span "nominal-bool.agent" 4)
        Push(LBool true, span "nominal-bool.agent" 5)
        Call("CheckedFlag.construct", span "nominal-bool.agent" 6)
        Call("dup", span "nominal-bool.agent" 7)
        Call("CheckedFlag.unwrap", span "nominal-bool.agent" 8)
    ]
    compareSuccessfulCase "nominal-bool-cases" "nominal-bool-cases" boolBody [
        NamedValue("Permission", BoolValue true); BoolValue true
        NamedValue("CheckedFlag", BoolValue true); BoolValue true
    ]

    let roundTrip =
        wordEntry "route-roundtrip" [ TNamed "RouteId" ] [ TNamed "RouteId" ] Set.empty [
            Call("route-id.raw", span "route-roundtrip.agent" 1)
            Call("route-id.create", span "route-roundtrip.agent" 2)
        ]
    let routeContext =
        contextWithScalarDefinitions [ roundTrip ] [
            scalarDefinition "RouteId" TInt None, "route-id.create", "route-id.raw"
        ]
    let routeBody = compileBody routeContext "nominal-branch-call" [
        Push(LInt 10L, span "nominal-route.agent" 1)
        Call("route-id.create", span "nominal-route.agent" 2)
        Let("saved", span "nominal-route.agent" 3)
        Push(LBool false, span "nominal-route.agent" 4)
        If(
            [ Push(LInt 20L, span "nominal-route.agent" 5); Call("route-id.create", span "nominal-route.agent" 6) ],
            [ Load("saved", span "nominal-route.agent" 7) ],
            span "nominal-route.agent" 4)
        Call("route-roundtrip", span "nominal-route.agent" 8)
        Call("dup", span "nominal-route.agent" 9)
        Push(LBool true, span "nominal-route.agent" 10)
        Call("swap", span "nominal-route.agent" 11)
        Call("route-id.raw", span "nominal-route.agent" 12)
        Push(LInt 77L, span "nominal-route.agent" 13)
        Call("route-id.create", span "nominal-route.agent" 14)
        Call("dup", span "nominal-route.agent" 15)
        Call("drop", span "nominal-route.agent" 16)
        Call("drop", span "nominal-route.agent" 17)
        Push(LInt 1L, span "nominal-route.agent" 18)
        Call("route-id.create", span "nominal-route.agent" 19)
        Push(LInt 1L, span "nominal-route.agent" 20)
        Call("route-id.create", span "nominal-route.agent" 21)
        Call("equals", span "nominal-route.agent" 22)
        Push(LInt 1L, span "nominal-route.agent" 23)
        Call("route-id.create", span "nominal-route.agent" 24)
        Push(LInt 2L, span "nominal-route.agent" 25)
        Call("route-id.create", span "nominal-route.agent" 26)
        Call("equals", span "nominal-route.agent" 27)
    ]
    compareSuccessfulCase "nominal-branch-call" "nominal-branch-call" routeBody [
        NamedValue("RouteId", IntValue 10L); BoolValue true; IntValue 10L
        BoolValue true; BoolValue false
    ]

    let wrongNominal =
        errorOf (fun () ->
            compileBody intContext "wrong-nominal" [
                Push(LInt 5L, span "wrong-nominal.agent" 1)
                Call("OrderId.create", span "wrong-nominal.agent" 2)
                Call("PositiveId.unwrap", span "wrong-nominal.agent" 3)
            ]
            |> ignore)
    check "a scalar accessor rejects a different nominal key" (wrongNominal.Code.StartsWith("TYPE_", StringComparison.Ordinal))

    let rejectAllValidatorBase =
        wordEntry "is-positive?" [ TInt ] [ TBool ] Set.empty [
            Call("drop", span "reject-positive.agent" 1)
            Push(LBool false, span "reject-positive.agent" 2)
        ]
    let replacedValidator =
        { rejectAllValidatorBase with
            Definition = { rejectAllValidatorBase.Definition with Revision = 2 }
            Revision = 2 }
    let originalSnapshotBody = compileBody intContext "frozen-validator" [
        Push(LInt 3L, span "snapshot-validator.agent" 1)
        Call("PositiveId.construct", span "snapshot-validator.agent" 2)
    ]
    let replacementContext =
        contextWithScalarDefinitions [ replacedValidator ] [
            scalarDefinition "PositiveId" TInt (Some "is-positive?"), "PositiveId.construct", "PositiveId.unwrap"
        ]
    let replacementBody = compileBody replacementContext "replacement-validator" [
        Push(LInt 3L, span "replacement-validator.agent" 1)
        Call("PositiveId.construct", span "replacement-validator.agent" 2)
    ]
    compareErrorCase "refinement-failed" "replacement-validator" (NativeDiagnosticSources.fromLoweringContext replacementContext) replacementBody
    let frozenExpected = [ NamedValue("PositiveId", IntValue 3L) ]
    check "the interpreter uses the validator frozen in the verified program" (interpreterResult "frozen-validator" originalSnapshotBody = frozenExpected)
    for optimization in [ LlvmOptimization.O0; LlvmOptimization.O2 ] do
        use native =
            compileNativeWithSources "frozen-validator" optimization (NativeDiagnosticSources.fromLoweringContext intContext) originalSnapshotBody
        check ($"{optimization} old native body retains its validator after replacement") (native.Execute "frozen-validator" |> fun result -> result.Values = frozenExpected)

    let zeroOutputBody = compileBody intContext "zero-output-validator" [
        Push(LInt 1L, span "zero-output-validator.agent" 1)
        Call("PositiveId.construct", span "zero-output-validator.agent" 2)
        Call("drop", span "zero-output-validator.agent" 3)
    ]
    check "zero-output validator independent fixture" (fixtureValues "zero-output-validator" = [])
    let interpretedZeroOutput, expectedZeroOutputSteps = interpreterResultAndSteps "zero-output-validator" zeroOutputBody
    check "zero-output validator interpreter result" (interpretedZeroOutput = [])
    for optimization in [ LlvmOptimization.O0; LlvmOptimization.O2 ] do
        use native =
            compileNativeWithSources "zero-output-validator" optimization (NativeDiagnosticSources.fromLoweringContext intContext) zeroOutputBody
        let actual = native.Execute "zero-output-validator"
        check ($"{optimization} zero-output body keeps its logical output count") (native.OutputCount = 0)
        check ($"{optimization} validator Bool result has workspace capacity") (native.WorkspaceCapacity = 1)
        check ($"{optimization} zero-output validator preserves fuel accounting") (actual.Values = interpretedZeroOutput && actual.StepsConsumed = expectedZeroOutputSteps)

let private testNominalScalarDiagnosticsAndRejections () =
    let positiveValidator =
        wordEntry "is-positive?" [ TInt ] [ TBool ] Set.empty [
            Push(LInt 0L, span "is-positive.agent" 1)
            Call("int.greater-than", span "is-positive.agent" 2)
        ]
    let positiveContext =
        contextWithScalarDefinitions [ positiveValidator ] [
            scalarDefinition "PositiveId" TInt (Some "is-positive?"), "PositiveId.construct", "PositiveId.unwrap"
        ]
    let falseRefinement = compileBody positiveContext "false-refinement" [
        Push(LInt -1L, span "false-refinement.agent" 1)
        Call("PositiveId.construct", span "false-refinement.agent" 2)
    ]
    compareErrorCase "refinement-failed" "false-refinement" (NativeDiagnosticSources.fromLoweringContext positiveContext) falseRefinement

    let boolContext =
        contextWithScalarDefinitions [] [
            scalarDefinition "RejectTrue" TBool (Some "bool.not"), "RejectTrue.wrap", "RejectTrue.value"
        ]
    let boolFalseRefinement = compileBody boolContext "bool-false-refinement" [
        Push(LBool true, span "bool-false-refinement.agent" 1)
        Call("RejectTrue.wrap", span "bool-false-refinement.agent" 2)
    ]
    compareErrorCase "bool-refinement-failed" "bool-false-refinement" (NativeDiagnosticSources.fromLoweringContext boolContext) boolFalseRefinement

    let overflowValidator =
        wordEntry "checked-positive?" [ TInt ] [ TBool ] Set.empty [
            Push(LInt Int64.MaxValue, span "checked-positive.agent" 1)
            Call("add", span "checked-positive.agent" 2)
            Push(LInt 0L, span "checked-positive.agent" 3)
            Call("int.greater-than", span "checked-positive.agent" 4)
        ]
    let overflowContext =
        contextWithScalarDefinitions [ overflowValidator ] [
            scalarDefinition "CheckedId" TInt (Some "checked-positive?"), "CheckedId.construct", "CheckedId.unwrap"
        ]
    let validatorFailure = compileBody overflowContext "validator-arithmetic-failure" [
        Push(LInt 1L, span "validator-arithmetic.agent" 1)
        Call("CheckedId.construct", span "validator-arithmetic.agent" 2)
    ]
    compareErrorCase "validator-arithmetic-failure" "validator-arithmetic-failure" (NativeDiagnosticSources.fromLoweringContext overflowContext) validatorFailure

    let unsupportedValidator =
        wordEntry "has-unsupported-branch?" [ TInt ] [ TBool ] Set.empty [
            Push(LBool false, span "unsupported-validator.agent" 1)
            If(
                [ ConstructContainer(OptionNone, [ TInt ], span "unsupported-validator.agent" 2)
                  Call("drop", span "unsupported-validator.agent" 3)
                  Call("drop", span "unsupported-validator.agent" 4)
                  Push(LBool true, span "unsupported-validator.agent" 5) ],
                [ Call("drop", span "unsupported-validator.agent" 6)
                  Push(LBool false, span "unsupported-validator.agent" 7) ],
                span "unsupported-validator.agent" 1)
        ]
    let unsupportedValidatorContext =
        contextWithScalarDefinitions [ unsupportedValidator ] [
            scalarDefinition "UntakenValidator" TInt (Some "has-unsupported-branch?"), "UntakenValidator.make", "UntakenValidator.value"
        ]
    let unsupportedValidatorBody = compileBody unsupportedValidatorContext "unsupported-validator-closure" [
        Push(LInt 1L, span "unsupported-validator-main.agent" 1)
        Call("UntakenValidator.make", span "unsupported-validator-main.agent" 2)
    ]
    let unsupportedValidatorError = errorOf (fun () -> LlvmAot.emit unsupportedValidatorBody |> ignore)
    check "unsupported IR in an untaken validator branch is rejected" (
        unsupportedValidatorError.Code = "IR_LLVM_UNSUPPORTED_OPERATION"
        && unsupportedValidatorError.Span = Some(span "unsupported-validator.agent" 2))

    for name, baseType, literal in [
        "FloatTag", TFloat, LFloat 1.5
        "StringTag", TString, LString "value"
    ] do
        let context = contextWithScalars [] [ scalarDefinition name baseType None ]
        let source = span "unsupported-nominal-base.agent" 1
        let body = compileBody context ("unsupported-" + name) [ Push(literal, source); Call(name + ".make", source) ]
        let unsupportedError = errorOf (fun () -> LlvmAot.emit body |> ignore)
        check (name + " nominal base stays outside the native slice") (
            unsupportedError.Code = "IR_LLVM_UNSUPPORTED_TYPE"
            && unsupportedError.Actual |> List.exists (fun actual -> actual.Contains(IrTypes.format (if baseType = TFloat then IrFloat else IrString))))

    let unitContext = contextWithScalars [] [ scalarDefinition "UnitTag" TUnit None ]
    let unitBaseError = errorOf (fun () -> Compiler.compileIrProgram unitContext |> ignore)
    check "Unit scalar declaration remains rejected by the compiler's current type rule" (unitBaseError.Code = "TYPE_UNSUPPORTED_SCALAR_BASE")

let private testNominalScalarDepth () =
    let positiveValidator =
        wordEntry "is-positive?" [ TInt ] [ TBool ] Set.empty [
            Push(LInt 0L, span "depth-validator.agent" 1)
            Call("int.greater-than", span "depth-validator.agent" 2)
        ]

    let runCase typeName baseType validator finalIndex (literal: Literal) shouldFail =
        let scalar = scalarDefinition typeName baseType validator
        let deepWords =
            [ 0 .. finalIndex ]
            |> List.map (fun index ->
                let name = $"deep.scalar.{index}"
                let body =
                    if index < finalIndex then [ Call($"deep.scalar.{index + 1}", span (name + ".agent") 2) ]
                    else
                        [ Push(literal, span (name + ".agent") 2)
                          Call(typeName + ".make", span (name + ".agent") 3)
                          Call("drop", span (name + ".agent") 4) ]
                wordEntry name [] [] Set.empty body)
        let context =
            let validatorWords = if validator = Some "is-positive?" then [ positiveValidator ] else []
            let extraWords = validatorWords @ deepWords
            contextWithScalars extraWords [ scalar ]
        let body = compileBody context ("deep-scalar-" + typeName) [ Call("deep.scalar.0", span "deep-scalar-entry.agent" 1) ]
        let sources = NativeDiagnosticSources.fromLoweringContext context
        if shouldFail then
            let mutable interpretedSteps = 0
            let interpreterHost =
                { noOpHost () with
                    ChargeInstruction = fun _ _ -> interpretedSteps <- interpretedSteps + 1
                    WordDefinitionSpan = fun word -> sources.WordDefinitionSpans.TryFind word
                    PrimitiveDefinitionSpan = fun word -> sources.PrimitiveDefinitionSpans.TryFind word }
            let interpretedError = errorOf (fun () -> IrInterpreter.executeBody interpreterHost "deep-scalar-entry" body |> ignore)
            let expectedFixture = fixtureError "validator-depth-limit"
            let expectedWord = validator |> Option.defaultValue ""
            let expectedSpan =
                if expectedWord = "bool.not" then sources.PrimitiveDefinitionSpans.TryFind expectedWord
                else sources.WordDefinitionSpans.TryFind expectedWord
            check (typeName + " interpreter rejects validator transition at depth 65") (
                interpretedError.Code = expectedFixture.GetProperty("code").GetString()
                && interpretedError.Word = Some expectedWord
                && interpretedError.Span = expectedSpan
                && interpretedError.Actual.IsEmpty)
            check (typeName + " interpreter charges no extra generated/validator instruction") (interpretedSteps = 66)
            for optimization in [ LlvmOptimization.O0; LlvmOptimization.O2 ] do
                use native = compileNativeWithSources ("deep-scalar-" + typeName) optimization sources body
                let actualError = errorOf (fun () -> native.Execute "deep-scalar-entry" |> ignore)
                check ($"{optimization} {typeName} depth error matches interpreter") (actualError = interpretedError)
                let nativeStatus, nativeSteps = rawExecutionStatusAndSteps native
                check ($"{optimization} {typeName} failed depth guard retains diagnostic status") (nativeStatus = NativeAbi.StatusDiagnostic)
                check ($"{optimization} {typeName} generated/validator depth charges match interpreter") (nativeSteps = interpretedSteps)
        else
            let interpreted, expectedSteps = interpreterResultAndSteps "deep-scalar-entry" body
            let expectedDepthSteps = if validator = Some "bool.not" then 66 else 67
            check (typeName + " interpreter permits the validator at depth 64") (interpreted = [] && expectedSteps = expectedDepthSteps)
            for optimization in [ LlvmOptimization.O0; LlvmOptimization.O2 ] do
                use native = compileNativeWithSources ("deep-scalar-" + typeName) optimization sources body
                let actual = native.Execute "deep-scalar-entry"
                check ($"{optimization} {typeName} generated constructor depth boundary") (actual.Values = interpreted)
                check ($"{optimization} {typeName} generated constructor step count") (actual.StepsConsumed = expectedSteps)

    runCase "DepthTag" TInt None 63 (LInt 1L) false
    runCase "RefinedDepthTag" TInt (Some "is-positive?") 63 (LInt 1L) true
    runCase "BoundaryDepthTag" TInt (Some "is-positive?") 61 (LInt 1L) false
    runCase "PrimitiveDepthFlag" TBool (Some "bool.not") 63 (LBool true) true
    runCase "PrimitiveBoundaryFlag" TBool (Some "bool.not") 62 (LBool false) false

let private recordField name fieldType =
    { Name = name
      Type = fieldType }

let private recordDefinition name fields =
    { Name = name
      Fields = fields
      SourceText = "record " + name
      Span = span (name + ".agent") 1 }

let private contextWithRecordDefinitions extraWords (records: RecordDefinition list) scalarDefinitions =
    let recordMap = records |> List.map (fun record -> record.Name, record) |> Map.ofList
    contextWithDefinitions extraWords recordMap scalarDefinitions

let private compareRecordErrorCase (fixtureName: string) (executionName: string) (body: VerifiedIrBody) =
    let expectedFixture = recordFixtureError fixtureName
    let interpreted = errorOf (fun () -> interpreterResult executionName body |> ignore)
    let expectedStringList (propertyName: string) =
        expectedFixture.GetProperty(propertyName).EnumerateArray()
        |> Seq.map (fun value -> value.GetString())
        |> Seq.toList
    let expectedSpan =
        let fixture = expectedFixture.GetProperty("span")
        if fixture.ValueKind = JsonValueKind.Null then None
        else
            Some
                { File = fixture.GetProperty("file").GetString()
                  Line = fixture.GetProperty("line").GetInt32()
                  Column = fixture.GetProperty("column").GetInt32()
                  Length = fixture.GetProperty("length").GetInt32() }
    let matchesFixture =
        interpreted.Code = expectedFixture.GetProperty("code").GetString()
        && interpreted.Word = Some(expectedFixture.GetProperty("word").GetString())
        && interpreted.Message = expectedFixture.GetProperty("message").GetString()
        && interpreted.Expected = expectedStringList "expected"
        && interpreted.Actual = expectedStringList "actual"
        && interpreted.Span = expectedSpan
    if not matchesFixture then
        let actualDiagnostic =
            [ "code=" + interpreted.Code
              "word=" + sprintf "%A" interpreted.Word
              "message=" + sprintf "%A" interpreted.Message
              "expected=" + sprintf "%A" interpreted.Expected
              "actual=" + sprintf "%A" interpreted.Actual
              "span=" + sprintf "%A" interpreted.Span ]
            |> String.concat "; "
        failwith (
            fixtureName
            + " independent diagnostic fixture mismatch. Fixture: "
            + expectedFixture.GetRawText()
            + ". Interpreter: "
            + actualDiagnostic)
    check (fixtureName + " independent diagnostic fixture") matchesFixture
    for optimization in [ LlvmOptimization.O0; LlvmOptimization.O2 ] do
        use native = compileNative fixtureName optimization body
        let actual = errorOf (fun () -> native.Execute executionName |> ignore)
        check ($"{fixtureName} {optimization} complete diagnostic parity") (actual = interpreted)

let private testRecordConformance () =
    let positiveValidator =
        wordEntry "is-positive?" [ TInt ] [ TBool ] Set.empty [
            Push(LInt 0L, span "positive-field.agent" 1)
            Call("int.greater-than", span "positive-field.agent" 2)
        ]
    let ticketRecords = [
        recordDefinition "Leaf" [ recordField "active" TBool; recordField "value" TInt ]
        recordDefinition "Ticket" [ recordField "owner" (TNamed "PositiveId"); recordField "leaf" (TNamed "Leaf") ]
    ]
    let ticketContext =
        contextWithRecordDefinitions [ positiveValidator ] ticketRecords [
            scalarDefinition "PositiveId" TInt (Some "is-positive?"), "PositiveId.new", "PositiveId.value"
        ]
    let ticketBody = compileBody ticketContext "record-nested-fields" [
        Push(LInt 7L, span "ticket.agent" 1)
        Call("PositiveId.new", span "ticket.agent" 2)
        Let("owner", span "ticket.agent" 3)
        Push(LBool true, span "ticket.agent" 5)
        Push(LInt 17L, span "ticket.agent" 6)
        Call("leaf.new", span "ticket.agent" 7)
        Let("base-leaf", span "ticket.agent" 8)
        Push(LBool false, span "ticket.agent" 10)
        If(
            [ Push(LBool false, span "ticket.agent" 11); Push(LInt 99L, span "ticket.agent" 12); Call("leaf.new", span "ticket.agent" 13) ],
            [ Load("base-leaf", span "ticket.agent" 14) ],
            span "ticket.agent" 10)
        Let("chosen-leaf", span "ticket.agent" 15)
        Load("owner", span "ticket.agent" 17)
        Load("chosen-leaf", span "ticket.agent" 18)
        Call("ticket.new", span "ticket.agent" 19)
        Call("dup", span "ticket.agent" 20)
        Call("ticket.leaf", span "ticket.agent" 21)
        Call("leaf.value", span "ticket.agent" 22)
        Call("swap", span "ticket.agent" 23)
        Call("dup", span "ticket.agent" 24)
        Call("ticket.owner", span "ticket.agent" 25)
        Call("PositiveId.value", span "ticket.agent" 26)
    ]
    let leaf = RecordValue("Leaf", Map.ofList [ "active", BoolValue true; "value", IntValue 17L ])
    let ticket = RecordValue("Ticket", Map.ofList [ "leaf", leaf; "owner", NamedValue("PositiveId", IntValue 7L) ])
    compareSuccessfulRecordCase "record-nested-fields" "record-nested-fields" ticketBody [ IntValue 17L; ticket; IntValue 7L ]

    let sharingContext =
        contextWithRecordDefinitions [] [
            recordDefinition "Leaf" [ recordField "value" TInt ]
            recordDefinition "Box" [ recordField "child" (TNamed "Leaf"); recordField "tag" TInt ]
            recordDefinition "Empty" []
            recordDefinition "Orphan" [ recordField "value" TInt ]
        ] []
    let sharingBody = compileBody sharingContext "record-sharing" [
        Push(LInt 99L, span "sharing.agent" 1)
        Call("orphan.new", span "sharing.agent" 2)
        Call("drop", span "sharing.agent" 3)
        Push(LInt 5L, span "sharing.agent" 4)
        Call("leaf.new", span "sharing.agent" 5)
        Let("shared", span "sharing.agent" 6)
        Load("shared", span "sharing.agent" 7)
        Push(LInt 10L, span "sharing.agent" 8)
        Call("box.new", span "sharing.agent" 9)
        Let("first", span "sharing.agent" 10)
        Load("shared", span "sharing.agent" 11)
        Push(LInt 20L, span "sharing.agent" 12)
        Call("box.new", span "sharing.agent" 13)
        Let("second", span "sharing.agent" 14)
        Push(LInt 5L, span "sharing.agent" 15)
        Call("leaf.new", span "sharing.agent" 16)
        Let("equal-but-distinct", span "sharing.agent" 17)
        Load("first", span "sharing.agent" 19)
        Load("second", span "sharing.agent" 20)
        Load("shared", span "sharing.agent" 21)
        Load("equal-but-distinct", span "sharing.agent" 22)
        Load("shared", span "sharing.agent" 23)
        Load("equal-but-distinct", span "sharing.agent" 24)
        Call("equals", span "sharing.agent" 25)
        Load("first", span "sharing.agent" 26)
        Load("second", span "sharing.agent" 27)
        Call("equals", span "sharing.agent" 28)
        Call("empty.new", span "sharing.agent" 29)
    ]
    let sharedLeaf = RecordValue("Leaf", Map.ofList [ "value", IntValue 5L ])
    let firstBox = RecordValue("Box", Map.ofList [ "child", sharedLeaf; "tag", IntValue 10L ])
    let secondBox = RecordValue("Box", Map.ofList [ "child", sharedLeaf; "tag", IntValue 20L ])
    let distinctEqualLeaf = RecordValue("Leaf", Map.ofList [ "value", IntValue 5L ])
    compareSuccessfulRecordCase "record-sharing" "record-sharing" sharingBody [ firstBox; secondBox; sharedLeaf; distinctEqualLeaf; BoolValue true; BoolValue false; RecordValue("Empty", Map.empty) ]

    let emptyContext = contextWithRecordDefinitions [] [ recordDefinition "Empty" [] ] []
    let emptyBody = compileBody emptyContext "record-empty" [ Call("empty.new", span "empty-record.agent" 1) ]
    compareSuccessfulRecordCase "record-empty" "record-empty" emptyBody [ RecordValue("Empty", Map.empty) ]
    let emptyDiscarded = compileBody emptyContext "record-empty-discarded" [
        Call("empty.new", span "empty-record.agent" 1)
        Call("drop", span "empty-record.agent" 2)
    ]
    use emptyDiscardedNative = compileNative "record-empty-discarded" LlvmOptimization.O0 emptyDiscarded
    let emptyDiscardedResult = emptyDiscardedNative.Execute "record-empty-discarded"
    let emptyDiscardedFixture = recordFixtureRoot.RootElement.GetProperty("record-empty-discarded")
    check "zero-output empty-record execution still reserves one helper workspace slot" (
        emptyDiscardedNative.OutputCount = 0
        && emptyDiscardedNative.OutputCapacity = 0
        && emptyDiscardedNative.WorkspaceCapacity = emptyDiscardedFixture.GetProperty("requiredWorkspaceCapacity").GetInt32()
        && emptyDiscardedResult.Values.IsEmpty)
    sharingBody, [ firstBox; secondBox; sharedLeaf; distinctEqualLeaf; BoolValue true; BoolValue false; RecordValue("Empty", Map.empty) ]

let private testRecordValueMetrics () =
    let depthCase maxIndex executionName =
        let records =
            [ 0 .. maxIndex ]
            |> List.map (fun index ->
                if index = 0 then recordDefinition "D0" [ recordField "value" TInt ]
                else recordDefinition ($"D{index}") [ recordField "child" (TNamed ($"D{index - 1}")) ])
        let context = contextWithRecordDefinitions [] records []
        let expressions = ResizeArray<Expr>()
        expressions.Add(Push(LInt 1L, span "record-depth.agent" 1))
        expressions.Add(Call("d0.new", span "record-depth.agent" 2))
        expressions.Add(Let("current", span "record-depth.agent" 3))
        for index in 1 .. maxIndex do
            expressions.Add(Load("current", span "record-depth.agent" (index + 4)))
            expressions.Add(Call(($"d{index}.new"), span "record-depth.agent" (index + 2)))
            expressions.Add(Let("current", span "record-depth.agent" (index + 6)))
        if executionName = "record-depth-256" then
            expressions.Add(Load("current", span "record-depth.agent" (maxIndex + 8)))
        compileBody context executionName (List.ofSeq expressions)

    let depth256 = depthCase 254 "record-depth-256"
    let depthSpec = recordFixtureRoot.RootElement.GetProperty("record-depth-256")
    let mutable expectedDeepValue = RecordValue("D0", Map.ofList [ "value", IntValue 1L ])
    for index in 1 .. depthSpec.GetProperty("depth").GetInt32() - 2 do
        expectedDeepValue <- RecordValue($"D{index}", Map.ofList [ "child", expectedDeepValue ])
    check "depth-256 fixture names the independently constructed root" (
        Types.ofValue expectedDeepValue = TNamed(depthSpec.GetProperty("rootType").GetString())
        && depthSpec.GetProperty("leaf").GetString() = "D0 { value = 1 }")
    let interpreted256, expectedSteps = interpreterResultAndSteps "record-depth-256" depth256
    check "record value at the depth-256 boundary matches the independent expected value" (interpreted256 = [ expectedDeepValue ])
    for optimization in [ LlvmOptimization.O0; LlvmOptimization.O2 ] do
        use native = compileNative "record-depth-256" optimization depth256
        use retained = native.ExecuteRetained "record-depth-256"
        check ($"{optimization} record depth-256 retained decode") (retained.Decode() = [ expectedDeepValue ] && retained.StepsConsumed = expectedSteps)

    let depth257 = depthCase 255 "record-depth-257"
    compareRecordErrorCase "record-depth-257" "record-depth-257" depth257

    let untakenRecords =
        [ 0 .. 255 ]
        |> List.map (fun index ->
            if index = 0 then recordDefinition "D0" [ recordField "value" TInt ]
            else recordDefinition ($"D{index}") [ recordField "child" (TNamed ($"D{index - 1}")) ])
    let untakenContext = contextWithRecordDefinitions [] untakenRecords []
    let untakenThen = ResizeArray<Expr>()
    untakenThen.Add(Push(LInt 1L, span "record-untaken-depth.agent" 2))
    untakenThen.Add(Call("d0.new", span "record-untaken-depth.agent" 3))
    for index in 1 .. 255 do
        untakenThen.Add(Call($"d{index}.new", span "record-untaken-depth.agent" (index + 3)))
    untakenThen.Add(Call("drop", span "record-untaken-depth.agent" 260))
    untakenThen.Add(Push(LInt 1L, span "record-untaken-depth.agent" 261))
    let overLimitUntaken = compileBody untakenContext "record-over-limit-untaken-branch" [
        Push(LBool false, span "record-untaken-depth.agent" 1)
        If(List.ofSeq untakenThen, [ Push(LInt 2L, span "record-untaken-depth.agent" 262) ], span "record-untaken-depth.agent" 1)
    ]
    compareSuccessfulRecordCase "record-over-limit-untaken-branch" "record-over-limit-untaken-branch" overLimitUntaken [ IntValue 2L ]

    let aggregateNames = [ 0 .. 14 ] |> List.map (fun index -> string (char (int 'A' + index)))
    let aggregateRecords =
        aggregateNames
        |> List.mapi (fun index name ->
            if index = 0 then recordDefinition name [ recordField "x" TInt ]
            else recordDefinition name [ recordField "left" (TNamed aggregateNames[index - 1]); recordField "right" (TNamed aggregateNames[index - 1]) ])
    let aggregateContext = contextWithRecordDefinitions [] aggregateRecords []
    let aggregateExpressions = ResizeArray<Expr>()
    aggregateExpressions.Add(Push(LInt 1L, span "record-aggregate.agent" 1))
    aggregateExpressions.Add(Call("a.new", span "record-aggregate.agent" 2))
    for index in 1 .. aggregateNames.Length - 1 do
        aggregateExpressions.Add(Call("dup", span "record-aggregate.agent" (index + 2)))
        aggregateExpressions.Add(Call(lowerFirst aggregateNames[index] + ".new", span "record-aggregate.agent" (index + 3)))
    aggregateExpressions.Add(Let("saved", span "record-aggregate.agent" 30))
    aggregateExpressions.Add(Push(LInt 1L, span "record-aggregate.agent" 31))
    aggregateExpressions.Add(Call("a.new", span "record-aggregate.agent" 31))
    for index in 1 .. aggregateNames.Length - 1 do
        aggregateExpressions.Add(Call("dup", span "record-aggregate.agent" 31))
        let sourceColumn = if index = aggregateNames.Length - 1 then 32 else 31
        aggregateExpressions.Add(Call(lowerFirst aggregateNames[index] + ".new", span "record-aggregate.agent" sourceColumn))
    let aggregateBody = compileBody aggregateContext "record-aliased-aggregate-limit" (List.ofSeq aggregateExpressions)
    let aggregateFixture = recordFixtureRoot.RootElement.GetProperty("record-aliased-aggregate-limit")
    let aggregateMetrics = aggregateFixture.GetProperty("metricCalculation")
    check "aggregate fixture independently identifies the first output-size crossing at dup M" (
        aggregateMetrics.GetProperty("savedRootExpandedNodes").GetInt32() = 49_151
        && aggregateMetrics.GetProperty("savedRootEstimatedOutputBytes").GetInt32() = 6_094_686
        && aggregateMetrics.GetProperty("secondRootType").GetString() = "M"
        && aggregateMetrics.GetProperty("secondRootExpandedNodes").GetInt32() = 12_287
        && aggregateMetrics.GetProperty("secondRootEstimatedOutputBytes").GetInt32() = 1_523_550
        && aggregateMetrics.GetProperty("combinedExpandedNodesAtDup").GetInt32() = 73_725
        && aggregateMetrics.GetProperty("combinedEstimatedOutputBytesAtDup").GetInt32() = 9_141_786
        && aggregateMetrics.GetProperty("firstFailingOperation").GetString() = "dup"
        && aggregateMetrics.GetProperty("firstFailingColumn").GetInt32() = 31
        && aggregateMetrics.GetProperty("combinedExpandedNodesAtDup").GetInt32() < 100_000
        && aggregateMetrics.GetProperty("combinedEstimatedOutputBytesAtDup").GetInt32() > 8_000_000)
    compareRecordErrorCase "record-aliased-aggregate-limit" "record-aliased-aggregate-limit" aggregateBody

    let nodeNames = [ 0 .. 15 ] |> List.map (fun index -> string (char (int 'A' + index)))
    let nodeRecords =
        nodeNames
        |> List.mapi (fun index name ->
            if index = 0 then recordDefinition name []
            else recordDefinition name [ recordField "l" (TNamed nodeNames[index - 1]); recordField "r" (TNamed nodeNames[index - 1]) ])
    let nodeContext = contextWithRecordDefinitions [] nodeRecords []
    let nodeExpressions = ResizeArray<Expr>()
    nodeExpressions.Add(Call("a.new", span "record-node-limit.agent" 1))
    for index in 1 .. nodeNames.Length - 1 do
        nodeExpressions.Add(Call("dup", span "record-node-limit.agent" (index + 1)))
        nodeExpressions.Add(Call(lowerFirst nodeNames[index] + ".new", span "record-node-limit.agent" (index + 2)))
    nodeExpressions.Add(Call("dup", span "record-node-limit.agent" 32))
    let nodeLimited = compileBody nodeContext "record-aliased-node-limit" (List.ofSeq nodeExpressions)
    let nodeFixture = recordFixtureError "record-aliased-node-limit"
    check "one aliased record root is below both limits before duplicated aggregate accounting" (
        nodeFixture.GetProperty("rootExpandedNodes").GetInt32() = 65535
        && nodeFixture.GetProperty("rootEstimatedOutputBytes").GetInt32() = 7470984
        && nodeFixture.GetProperty("rootExpandedNodes").GetInt32() < 100_000
        && nodeFixture.GetProperty("rootEstimatedOutputBytes").GetInt32() < 8_000_000)
    compareRecordErrorCase "record-aliased-node-limit" "record-aliased-node-limit" nodeLimited

    let largeName = String.replicate 334_000 "Q"
    let nominalContext = contextWithScalarDefinitions [] [ scalarDefinition largeName TInt None, largeName + ".new", largeName + ".value" ]
    let nominalAlias = compileBody nominalContext "nominal-aliased-output-limit" [
        Push(LInt 7L, span "nominal-output.agent" 1)
        Call(largeName + ".new", span "nominal-output.agent" 2)
        Call("dup", span "nominal-output.agent" 3)
    ]
    compareRecordErrorCase "nominal-aliased-output-limit" "nominal-aliased-output-limit" nominalAlias

let private testRecordPreflightRejections () =
    let unsupportedFixture = recordFixtureError "unsupported-record-fields"
    let unsupportedCases = unsupportedFixture.GetProperty("cases").EnumerateArray() |> Seq.map (fun value -> value.GetString()) |> Seq.toList
    let cases = [ "Float", TFloat; "String", TString; "List<Int>", TList TInt ]
    check "record fixture lists the unsupported field kinds independently" (unsupportedCases = [ "Float"; "String"; "List<Int>" ])
    for index, (typeName, fieldType) in List.indexed cases do
        let recordName = $"Unsupported{index}"
        let record = recordDefinition recordName [ recordField "payload" fieldType ]
        let context = contextWithRecordDefinitions [] [ record ] []
        let body =
            if fieldType = TFloat then
                compileBody context "unsupported-record-float-branch" [
                    Push(LBool false, span "unsupported-record.agent" 1)
                    If(
                        [ Push(LFloat 1.5, span "unsupported-record.agent" 2)
                          Call("unsupported0.new", span "unsupported-record.agent" 3)
                          Call("drop", span "unsupported-record.agent" 4)
                          Push(LInt 1L, span "unsupported-record.agent" 5) ],
                        [ Push(LInt 2L, span "unsupported-record.agent" 6) ],
                        span "unsupported-record.agent" 1)
                ]
            else compileBody context ("unsupported-record-" + string index) [ Push(LInt 1L, span "unsupported-record.agent" 1) ]
        let actual = errorOf (fun () -> LlvmAot.emit body |> ignore)
        check ($"{typeName} record field preflight rejects before Clang") (
            actual.Code = unsupportedFixture.GetProperty("code").GetString()
            && actual.Message = unsupportedFixture.GetProperty("message").GetString()
            && actual.Word = Some recordName
            && actual.Span.IsNone
            && actual.Expected = (unsupportedFixture.GetProperty("expected").EnumerateArray() |> Seq.map (fun value -> value.GetString()) |> Seq.toList)
            && actual.Actual = [ typeName ])

    let recursiveRecords = [
        recordDefinition "A" [ recordField "b" (TNamed "B") ]
        recordDefinition "B" [ recordField "a" (TNamed "A") ]
    ]
    let recursiveContext = contextWithRecordDefinitions [] recursiveRecords []
    let recursiveBody = compileBody recursiveContext "recursive-record-types" [ Push(LInt 1L, span "recursive-record.agent" 1) ]
    let recursive = errorOf (fun () -> LlvmAot.emit recursiveBody |> ignore)
    let recursiveFixture = recordFixtureError "recursive-record-types"
    let recursiveExpected (propertyName: string) =
        recursiveFixture.GetProperty(propertyName).EnumerateArray()
        |> Seq.map (fun value -> value.GetString())
        |> Seq.toList
    check "recursive record graph is rejected during native preflight with an independent cycle fixture" (
        recursive.Code = recursiveFixture.GetProperty("code").GetString()
        && recursive.Message = recursiveFixture.GetProperty("message").GetString()
        && recursive.Word = Some(recursiveFixture.GetProperty("word").GetString())
        && recursive.Span.IsNone
        && recursive.Expected = recursiveExpected "expected"
        && recursive.Actual = recursiveExpected "actual")

let private testRetainedOwnershipAndCapacity sharingBody expectedValues =
    use native = compileNative "record-retained-capacity" LlvmOptimization.O0 sharingBody
    let firstOrdinary = native.Execute "record-sharing"
    let secondOrdinary = native.Execute "record-sharing"
    check "repeated Execute calls on one compiled record program preserve type metadata" (
        firstOrdinary.Values = expectedValues && secondOrdinary.Values = expectedValues)
    use baseline = native.ExecuteRetained "record-sharing"
    let arenaFixture = recordFixtureRoot.RootElement.GetProperty("record-sharing-arenas")
    let scratchBytes = arenaFixture.GetProperty("scratchBytes").GetInt32()
    let scratchNodes = arenaFixture.GetProperty("scratchNodes").GetInt32()
    let retainedBytes = arenaFixture.GetProperty("retainedBytes").GetInt32()
    let retainedNodes = arenaFixture.GetProperty("retainedNodes").GetInt32()
    check "retained owner matches independent values and arena counts" (
        baseline.Decode() = expectedValues
        && baseline.RetainedByteCount = retainedBytes
        && baseline.RetainedNodeCount = retainedNodes
        && retainedBytes = 48
        && retainedNodes = 5)
    check "independent scratch arena sizing covers each constructor exactly once" (
        scratchBytes = 56 && scratchNodes = 6)

    let failureFor options =
        try
            use _result = native.ExecuteRetained("record-sharing", options = options)
            failwith "Expected NativeResourceLimitException."
        with
        | :? NativeResourceLimitException as failure -> failure

    let exactOptions = {
        NativeExecutionOptions.defaults with
            ScratchByteCapacity = Some scratchBytes
            ScratchNodeCapacity = Some scratchNodes
            RetainedByteCapacity = Some retainedBytes
            RetainedNodeCapacity = Some retainedNodes
    }
    use exact = native.ExecuteRetained("record-sharing", options = exactOptions)
    check "exact-fit scratch and retained capacities succeed" (
        exact.Decode() = expectedValues
        && exact.RetainedByteCount = retainedBytes
        && exact.RetainedNodeCount = retainedNodes)

    let scratchByteFailureOptions = { exactOptions with ScratchByteCapacity = Some(scratchBytes - 1) }
    let scratchByteFailure = failureFor scratchByteFailureOptions
    check "one-byte-short scratch reports exact required and available bytes" (
        scratchByteFailure.Code = "NATIVE_SCRATCH_CAPACITY"
        && scratchByteFailure.Arena = "scratch"
        && scratchByteFailure.RequiredBytes = scratchBytes
        && scratchByteFailure.AvailableBytes = scratchBytes - 1
        && scratchByteFailure.AvailableNodes = scratchNodes)

    let scratchNodeFailureOptions = { exactOptions with ScratchNodeCapacity = Some(scratchNodes - 1) }
    let scratchNodeFailure = failureFor scratchNodeFailureOptions
    check "one-node-short scratch reports exact required and available nodes" (
        scratchNodeFailure.Code = "NATIVE_SCRATCH_CAPACITY"
        && scratchNodeFailure.Arena = "scratch"
        && scratchNodeFailure.RequiredNodes = scratchNodes
        && scratchNodeFailure.AvailableNodes = scratchNodes - 1
        && scratchNodeFailure.AvailableBytes = scratchBytes)

    let retainedByteFailureOptions = { exactOptions with RetainedByteCapacity = Some(retainedBytes - 1) }
    let retainedByteFailure = failureFor retainedByteFailureOptions
    check "one-byte-short retained arena reports exact required and available bytes" (
        retainedByteFailure.Code = "NATIVE_RETAINED_CAPACITY"
        && retainedByteFailure.Arena = "retained"
        && retainedByteFailure.RequiredBytes = retainedBytes
        && retainedByteFailure.AvailableBytes = retainedBytes - 1
        && retainedByteFailure.RequiredNodes = retainedNodes
        && retainedByteFailure.AvailableNodes = retainedNodes)

    let retainedNodeFailureOptions = { exactOptions with RetainedNodeCapacity = Some(retainedNodes - 1) }
    let retainedNodeFailure = failureFor retainedNodeFailureOptions
    check "one-node-short retained arena reports exact required and available nodes" (
        retainedNodeFailure.Code = "NATIVE_RETAINED_CAPACITY"
        && retainedNodeFailure.Arena = "retained"
        && retainedNodeFailure.RequiredNodes = retainedNodes
        && retainedNodeFailure.AvailableNodes = retainedNodes - 1
        && retainedNodeFailure.RequiredBytes = retainedBytes
        && retainedNodeFailure.AvailableBytes = retainedBytes)

    let execute, library = rawExecute native.LibraryPath
    try
        let rawFailure label scratchBytes scratchNodes retainedBytes retainedNodes expectedStatus expectedRequiredBytes expectedRequiredNodes =
            withRawExecutionBuffers native scratchBytes scratchNodes retainedBytes retainedNodes (fun context outputs status _scratch retained ->
                let sentinel = 0x5647382910ABCDEFL
                for index in 0 .. native.OutputCount do Marshal.WriteInt64(outputs, index * NativeAbi.SlotSize, sentinel + int64 index)
                execute.Invoke(context, outputs, native.OutputCount, status)
                let outputsUnchanged =
                    [ 0 .. native.OutputCount ]
                    |> List.forall (fun index -> Marshal.ReadInt64(outputs, index * NativeAbi.SlotSize) = sentinel + int64 index)
                check (label + " native status matches capacity failure") (Marshal.ReadInt32(status) = expectedStatus)
                check (label + " public outputs and canary remain unchanged") outputsUnchanged
                check (label + " native capacity diagnostics match required totals") (
                    Marshal.ReadInt64(context, NativeAbi.ContextErrorArgument0Offset) = int64 expectedRequiredBytes
                    && Marshal.ReadInt64(context, NativeAbi.ContextErrorArgument1Offset) = int64 expectedRequiredNodes)
                check (label + " failed promotion leaves retained owner empty") (
                    Marshal.ReadInt32(retained, NativeAbi.ArenaUsedOffset) = 0
                    && Marshal.ReadInt32(retained, NativeAbi.ArenaNodeCountOffset) = 0))

        rawFailure "scratch byte atomicity" (scratchBytes - 1) scratchNodes retainedBytes retainedNodes NativeAbi.StatusScratchCapacity scratchByteFailure.RequiredBytes scratchByteFailure.RequiredNodes
        rawFailure "scratch node atomicity" scratchBytes (scratchNodes - 1) retainedBytes retainedNodes NativeAbi.StatusScratchCapacity scratchNodeFailure.RequiredBytes scratchNodeFailure.RequiredNodes
        rawFailure "retained byte promotion atomicity" scratchBytes scratchNodes (retainedBytes - 1) retainedNodes NativeAbi.StatusRetainedCapacity retainedByteFailure.RequiredBytes retainedByteFailure.RequiredNodes
        rawFailure "retained node promotion atomicity" scratchBytes scratchNodes retainedBytes (retainedNodes - 1) NativeAbi.StatusRetainedCapacity retainedNodeFailure.RequiredBytes retainedNodeFailure.RequiredNodes
    finally
        NativeLibrary.Free library

    use reusableScratch = new NativeScratchArena(1_000_000, 10_000)
    let reusableOptions = { NativeExecutionOptions.defaults with ScratchArena = Some reusableScratch }
    let firstResult = native.ExecuteRetained("record-sharing", options = reusableOptions)
    let secondResult = native.ExecuteRetained("record-sharing", options = reusableOptions)
    let mutable firstDisposed = false
    try
        reusableScratch.PoisonAndClear()
        check "first retained decode survives scratch poisoning and reuse" (firstResult.Decode() = expectedValues)
        (firstResult :> IDisposable).Dispose()
        firstDisposed <- true
        (native :> IDisposable).Dispose()
        check "undecoded retained result survives peer disposal, scratch reuse, and compiled DLL disposal" (secondResult.Decode() = expectedValues)
    finally
        if not firstDisposed then (firstResult :> IDisposable).Dispose()
        (secondResult :> IDisposable).Dispose()

let private testTypedStateReentry () =
    let fixture = stateReentryFixtureRoot.RootElement
    let programFixture = fixture.GetProperty("program")
    let valuesInFixture (element: JsonElement) =
        element.EnumerateArray() |> Seq.map (fun value -> value.GetString()) |> Seq.toList
    let records = [
        recordDefinition "Leaf" [ recordField "value" TInt ]
        recordDefinition "Ticket" [ recordField "owner" (TNamed "PositiveId"); recordField "leaf" (TNamed "Leaf") ]
        recordDefinition "Envelope" [ recordField "ticket" (TNamed "Ticket"); recordField "count" TInt; recordField "active" TBool ]
        recordDefinition "Orphan" [ recordField "mark" TInt ]
    ]
    let positiveValidator = wordEntry "is-positive?" [ TInt ] [ TBool ] Set.empty [
        Push(LInt 0L, span "state-positive.agent" 1)
        Call("int.greater-than", span "state-positive.agent" 2)
    ]
    let context =
        contextWithRecordDefinitions [ positiveValidator ] records [
            scalarDefinition "PositiveId" TInt (Some "is-positive?"), "PositiveId.new", "PositiveId.value"
        ]
    let verifiedProgram = Compiler.compileIrProgram context
    let stateInputTypes = [ TNamed "Envelope"; TNamed "Envelope"; TInt; TBool; TUnit ]
    let initializationBody =
        Compiler.compileIrBodyAgainstProgram context verifiedProgram "state-init" [] [
            Push(LInt 7L, span "state-init.agent" 1)
            Call("PositiveId.new", span "state-init.agent" 2)
            Let("owner", span "state-init.agent" 3)
            Push(LInt 5L, span "state-init.agent" 4)
            Call("leaf.new", span "state-init.agent" 5)
            Let("leaf", span "state-init.agent" 6)
            Load("owner", span "state-init.agent" 7)
            Load("leaf", span "state-init.agent" 8)
            Call("ticket.new", span "state-init.agent" 9)
            Let("ticket", span "state-init.agent" 10)
            Load("ticket", span "state-init.agent" 11)
            Push(LInt 0L, span "state-init.agent" 12)
            Push(LBool false, span "state-init.agent" 13)
            Call("envelope.new", span "state-init.agent" 14)
            Let("state", span "state-init.agent" 15)
            Push(LInt 9L, span "state-init.agent" 16)
            Call("orphan.new", span "state-init.agent" 17)
            Let("orphan", span "state-init.agent" 18)
            Load("state", span "state-init.agent" 19)
            Load("ticket", span "state-init.agent" 20)
            Load("leaf", span "state-init.agent" 21)
            Load("orphan", span "state-init.agent" 22)
        ]
    let turnExpressions = [
        Call("drop", span "state-turn.agent" 1)
        Let("enabled", span "state-turn.agent" 2)
        Let("delta", span "state-turn.agent" 3)
        Let("other", span "state-turn.agent" 4)
        Let("state", span "state-turn.agent" 5)
        Load("state", span "state-turn.agent" 6)
        Call("envelope.ticket", span "state-turn.agent" 7)
        Let("ticket", span "state-turn.agent" 8)
        Load("ticket", span "state-turn.agent" 9)
        Load("state", span "state-turn.agent" 10)
        Call("envelope.count", span "state-turn.agent" 11)
        Load("delta", span "state-turn.agent" 12)
        Load("enabled", span "state-turn.agent" 13)
        If(
            [ Call("add", span "state-turn.agent" 14) ],
            [ Call("drop", span "state-turn.agent" 15) ],
            span "state-turn.agent" 16)
        Load("enabled", span "state-turn.agent" 17)
        Call("envelope.new", span "state-turn.agent" 18)
        Call("dup", span "state-turn.agent" 19)
        Load("ticket", span "state-turn.agent" 20)
    ]
    let turnBody = Compiler.compileIrBodyAgainstProgram context verifiedProgram "state-turn" stateInputTypes turnExpressions
    let roundTripBody = Compiler.compileIrBodyAgainstProgram context verifiedProgram "state-round-trip" stateInputTypes [
        Call("drop", span "state-round-trip.agent" 1)
        Call("drop", span "state-round-trip.agent" 2)
        Call("drop", span "state-round-trip.agent" 3)
    ]
    let postImportFailureBody = Compiler.compileIrBodyAgainstProgram context verifiedProgram "state-fail-after-import" stateInputTypes [
        Call("drop", span "state-fail-after-import.agent" 1)
        Call("drop", span "state-fail-after-import.agent" 2)
        Call("drop", span "state-fail-after-import.agent" 3)
        Call("drop", span "state-fail-after-import.agent" 4)
        Call("drop", span "state-fail-after-import.agent" 5)
        Push(LInt 1L, span "state-fail-after-import.agent" 6)
        Push(LInt 0L, span "state-fail-after-import.agent" 7)
        Call("divide", span "state-fail-after-import.agent" 8)
    ]
    let differentProgram = Compiler.compileIrProgram context
    let differentProgramTurn = Compiler.compileIrBodyAgainstProgram context differentProgram "state-turn-other-program" stateInputTypes turnExpressions
    check "initialization and all turns share one VerifiedIrProgram instance" (
        [ initializationBody; turnBody; roundTripBody; postImportFailureBody ]
        |> List.forall (fun body -> Object.ReferenceEquals(VerifiedIrBody.program body, verifiedProgram)))
    check "equivalent compiler snapshot still receives distinct program identity" (
        not (Object.ReferenceEquals(VerifiedIrBody.program turnBody, VerifiedIrBody.program differentProgramTurn)))

    let leaf = RecordValue("Leaf", Map.ofList [ "value", IntValue 5L ])
    let ticket = RecordValue("Ticket", Map.ofList [ "owner", NamedValue("PositiveId", IntValue 7L); "leaf", leaf ])
    let envelope active count = RecordValue("Envelope", Map.ofList [ "ticket", ticket; "count", IntValue count; "active", BoolValue active ])
    let orphan = RecordValue("Orphan", Map.ofList [ "mark", IntValue 9L ])
    let expectedInitialValues = [ envelope false 0; ticket; leaf; orphan ]
    let expectedRecoveryValues = [ envelope false 0; envelope false 0 ]
    let expectedTurnOneValues = [ envelope true 3; envelope true 3; ticket ]
    let expectedTurnTwoValues = [ envelope false 3; envelope false 3; ticket ]
    check "independent initialized and turned values match the state fixture" (
        formatValues expectedInitialValues = valuesInFixture (programFixture.GetProperty("initializationRoots"))
        && formatValues expectedTurnOneValues = valuesInFixture (fixture.GetProperty("turnOne").GetProperty("values"))
        && formatValues expectedTurnTwoValues = valuesInFixture (fixture.GetProperty("turnTwo").GetProperty("values")))
    check "independent post-failure recovery values match the round-trip fixture" (
        formatValues expectedRecoveryValues = valuesInFixture (fixture.GetProperty("roundTripCapacities").GetProperty("values")))

    let turnOneArguments = [
        IrEntryArgument.RetainedRoot 0
        IrEntryArgument.RetainedRoot 0
        IrEntryArgument.IntArgument 3L
        IrEntryArgument.BoolArgument true
        IrEntryArgument.UnitArgument
    ]
    let turnTwoArguments = [
        IrEntryArgument.RetainedRoot 0
        IrEntryArgument.RetainedRoot 1
        IrEntryArgument.IntArgument 5L
        IrEntryArgument.BoolArgument false
        IrEntryArgument.UnitArgument
    ]
    let wrongCountArguments = turnOneArguments |> List.take 4
    let wrongTypeArguments = [
        IrEntryArgument.RetainedRoot 0
        IrEntryArgument.RetainedRoot 0
        IrEntryArgument.BoolArgument true
        IrEntryArgument.BoolArgument false
        IrEntryArgument.UnitArgument
    ]
    let primitiveForNominalArguments = [
        IrEntryArgument.IntArgument 7L
        IrEntryArgument.RetainedRoot 0
        IrEntryArgument.IntArgument 3L
        IrEntryArgument.BoolArgument true
        IrEntryArgument.UnitArgument
    ]
    let wrongRootArguments = [
        IrEntryArgument.RetainedRoot 0
        IrEntryArgument.RetainedRoot 99
        IrEntryArgument.IntArgument 3L
        IrEntryArgument.BoolArgument true
        IrEntryArgument.UnitArgument
    ]
    let noOwnerArguments = turnOneArguments

    let runInterpreterInput (executionName: string) (body: VerifiedIrBody) (inputOwner: IrInterpreterResult option) (arguments: IrEntryArgument list) =
        let mutable steps = 0
        let host = { noOpHost () with ChargeInstruction = fun _ _ -> steps <- steps + 1 }
        let result = IrInterpreter.executeBodyWithInputs host executionName body inputOwner arguments
        result, steps
    let captureInterpreterError (executionName: string) (body: VerifiedIrBody) (inputOwner: IrInterpreterResult option) (arguments: IrEntryArgument list) =
        let mutable steps = 0
        let host = { noOpHost () with ChargeInstruction = fun _ _ -> steps <- steps + 1 }
        let diagnostic = errorOf (fun () -> IrInterpreter.executeBodyWithInputs host executionName body inputOwner arguments |> ignore)
        diagnostic, steps
    let captureNativeError (native: NativeCompiledProgram) (executionName: string) (inputOwner: NativeRetainedResult option) (arguments: IrEntryArgument list) =
        errorOf (fun () -> native.ExecuteRetainedWithInputs(executionName, inputOwner, arguments) |> ignore)
    let assertDiagnosticFixture fixtureName (diagnostic: Diagnostic) =
        let expected = stateReentryDiagnosticFixture fixtureName
        let strings (property: string) = expected.GetProperty(property).EnumerateArray() |> Seq.map (fun value -> value.GetString()) |> Seq.toList
        let expectedSpan =
            let spanValue = expected.GetProperty("span")
            if spanValue.ValueKind = JsonValueKind.Null then None
            else
                Some {
                    File = spanValue.GetProperty("file").GetString()
                    Line = spanValue.GetProperty("line").GetInt32()
                    Column = spanValue.GetProperty("column").GetInt32()
                    Length = spanValue.GetProperty("length").GetInt32()
                }
        check (fixtureName + " independent full diagnostic") (
            diagnostic.Code = expected.GetProperty("code").GetString()
            && diagnostic.Word = Some(expected.GetProperty("word").GetString())
            && diagnostic.Message = expected.GetProperty("message").GetString()
            && diagnostic.Expected = strings "expected"
            && diagnostic.Actual = strings "actual"
            && diagnostic.Span = expectedSpan)

    let coreInitial, _ = runInterpreterInput "state-init" initializationBody None []
    let coreInvalidCases = [
        "wrong-count", Some coreInitial, wrongCountArguments
        "wrong-type", Some coreInitial, wrongTypeArguments
        "primitive-cannot-satisfy-nominal", Some coreInitial, primitiveForNominalArguments
        "wrong-root", Some coreInitial, wrongRootArguments
        "root-without-owner", None, noOwnerArguments
    ]
    let coreDiagnostics =
        coreInvalidCases
        |> List.map (fun (fixtureName, inputOwner, arguments) ->
            let diagnostic, steps = captureInterpreterError "state-turn" turnBody inputOwner arguments
            assertDiagnosticFixture fixtureName diagnostic
            check (fixtureName + " interpreter rejects before charging instructions") (steps = 0)
            fixtureName, diagnostic)
        |> Map.ofList
    let coreMismatchDiagnostic, coreMismatchSteps =
        captureInterpreterError "state-turn-other-program" differentProgramTurn (Some coreInitial) turnOneArguments
    assertDiagnosticFixture "program-mismatch" coreMismatchDiagnostic
    check "program provenance mismatch rejects before interpreter instructions" (coreMismatchSteps = 0)

    let postImportFailureFixture = fixture.GetProperty("postImportFailure")
    let postImportFailureDiagnosticName = postImportFailureFixture.GetProperty("diagnostic").GetString()
    let corePostImportFailureDiagnostic, corePostImportFailureSteps =
        captureInterpreterError "state-fail-after-import" postImportFailureBody (Some coreInitial) turnOneArguments
    assertDiagnosticFixture postImportFailureDiagnosticName corePostImportFailureDiagnostic
    check "interpreter executes the failing operation after consuming all imported arguments" (
        corePostImportFailureSteps = postImportFailureFixture.GetProperty("expectedSteps").GetInt32())
    let coreRecovery, coreRecoverySteps = runInterpreterInput "state-round-trip" roundTripBody (Some coreInitial) turnOneArguments
    check "interpreter reuses the same opaque input after a post-import language failure" (
        coreRecoverySteps = fixture.GetProperty("roundTripCapacities").GetProperty("expectedSteps").GetInt32())

    let coreTurnOne, coreTurnOneSteps = runInterpreterInput "state-turn" turnBody (Some coreInitial) turnOneArguments
    coreInitial.Dispose()
    let coreTurnTwo, coreTurnTwoSteps = runInterpreterInput "state-turn" turnBody (Some coreTurnOne) turnTwoArguments
    coreTurnOne.Dispose()
    let coreDisposedDiagnostic, coreDisposedSteps = captureInterpreterError "state-turn" turnBody (Some coreTurnOne) turnTwoArguments
    assertDiagnosticFixture "disposed-owner" coreDisposedDiagnostic
    check "disposed owner rejects before interpreter instructions" (coreDisposedSteps = 0)
    let coreFinalValues = coreTurnTwo.Decode()
    check "interpreter retains opaque state over two turns and final decoding matches independent fixture" (
        coreFinalValues = expectedTurnTwoValues
        && formatValues coreFinalValues = valuesInFixture (fixture.GetProperty("turnTwo").GetProperty("values")))
    let coreRecoveryValues = coreRecovery.Decode()
    check "interpreter recovery result remains valid after its imported owner is disposed" (
        coreRecoveryValues = expectedRecoveryValues
        && formatValues coreRecoveryValues = valuesInFixture (fixture.GetProperty("roundTripCapacities").GetProperty("values")))
    let expectedTurnSteps = fixture.GetProperty("turnOne").GetProperty("expectedSteps").GetInt32()
    check "interpreter turn fuel matches the independent instruction count" (coreTurnOneSteps = expectedTurnSteps && coreTurnTwoSteps = expectedTurnSteps)
    coreTurnTwo.Dispose()
    coreRecovery.Dispose()

    let inputArena = programFixture.GetProperty("independentArenaTotals")
    let outputArena = programFixture.GetProperty("outputReachableTotals")
    let roundTripCapacity = fixture.GetProperty("roundTripCapacities")
    let inputBytes = inputArena.GetProperty("bytes").GetInt32()
    let inputNodes = inputArena.GetProperty("nodes").GetInt32()
    let outputBytes = outputArena.GetProperty("bytes").GetInt32()
    let outputNodes = outputArena.GetProperty("nodes").GetInt32()
    let turnOneFixture = fixture.GetProperty("turnOne")
    let turnTwoFixture = fixture.GetProperty("turnTwo")
    let expectedFinalSteps = turnTwoFixture.GetProperty("expectedSteps").GetInt32()
    let turnOneOptions = {
        NativeExecutionOptions.defaults with
            ScratchByteCapacity = Some(turnOneFixture.GetProperty("scratchBytes").GetInt32())
            ScratchNodeCapacity = Some(turnOneFixture.GetProperty("scratchNodes").GetInt32())
            RetainedByteCapacity = Some outputBytes
            RetainedNodeCapacity = Some outputNodes
    }
    let turnTwoOptions = {
        NativeExecutionOptions.defaults with
            ScratchByteCapacity = Some(turnTwoFixture.GetProperty("scratchBytes").GetInt32())
            ScratchNodeCapacity = Some(turnTwoFixture.GetProperty("scratchNodes").GetInt32())
            RetainedByteCapacity = Some outputBytes
            RetainedNodeCapacity = Some outputNodes
    }
    let roundTripArguments = turnOneArguments
    let roundTripOptions = {
        NativeExecutionOptions.defaults with
            ScratchByteCapacity = Some(roundTripCapacity.GetProperty("scratchBytes").GetInt32())
            ScratchNodeCapacity = Some(roundTripCapacity.GetProperty("scratchNodes").GetInt32())
            RetainedByteCapacity = Some(roundTripCapacity.GetProperty("retainedBytes").GetInt32())
            RetainedNodeCapacity = Some(roundTripCapacity.GetProperty("retainedNodes").GetInt32())
    }

    for optimization in [ LlvmOptimization.O0; LlvmOptimization.O2 ] do
        use nativeInit = compileNative "state-init" optimization initializationBody
        use nativeInitial = nativeInit.ExecuteRetainedWithInputs("state-init", None, [])
        (nativeInit :> IDisposable).Dispose()
        check ($"{optimization} initial retained graph matches independent full-owner bytes and nodes") (
            nativeInitial.RetainedByteCount = inputBytes
            && nativeInitial.RetainedNodeCount = inputNodes
            && inputArena.GetProperty("unselectedRootIndex").GetInt32() = 3)

        use validationNative = compileNative "state-turn-validation" optimization turnBody
        let nativeInvalidCases = [
            "wrong-count", Some nativeInitial, wrongCountArguments
            "wrong-type", Some nativeInitial, wrongTypeArguments
            "primitive-cannot-satisfy-nominal", Some nativeInitial, primitiveForNominalArguments
            "wrong-root", Some nativeInitial, wrongRootArguments
            "root-without-owner", None, noOwnerArguments
        ]
        for fixtureName, inputOwner, arguments in nativeInvalidCases do
            let nativeDiagnostic = captureNativeError validationNative "state-turn" inputOwner arguments
            check ($"{optimization} {fixtureName} native diagnostic matches interpreter") (
                nativeDiagnostic = Map.find fixtureName coreDiagnostics)
            assertDiagnosticFixture fixtureName nativeDiagnostic
        use mismatchNative = compileNative "state-turn-other-program" optimization differentProgramTurn
        let nativeMismatchDiagnostic = captureNativeError mismatchNative "state-turn-other-program" (Some nativeInitial) turnOneArguments
        check ($"{optimization} different-program native diagnostic matches interpreter") (nativeMismatchDiagnostic = coreMismatchDiagnostic)
        assertDiagnosticFixture "program-mismatch" nativeMismatchDiagnostic

        use roundTripNative = compileNative "state-round-trip" optimization roundTripBody
        let postImportScratchOptions = {
            NativeExecutionOptions.defaults with
                ScratchByteCapacity = Some(postImportFailureFixture.GetProperty("scratchBytes").GetInt32())
                ScratchNodeCapacity = Some(postImportFailureFixture.GetProperty("scratchNodes").GetInt32())
        }
        use postImportFailureNative = compileNativeWithSources "state-fail-after-import" optimization (NativeDiagnosticSources.fromLoweringContext context) postImportFailureBody
        let nativePostImportFailureDiagnostic =
            errorOf (fun () ->
                postImportFailureNative.ExecuteRetainedWithInputs(
                    "state-fail-after-import",
                    Some nativeInitial,
                    turnOneArguments,
                    options = postImportScratchOptions)
                |> ignore)
        check ($"{optimization} post-import language failure matches interpreter") (nativePostImportFailureDiagnostic = corePostImportFailureDiagnostic)
        assertDiagnosticFixture postImportFailureDiagnosticName nativePostImportFailureDiagnostic
        let exactRoundTrip = roundTripNative.ExecuteRetainedWithInputs("state-round-trip", Some nativeInitial, roundTripArguments, options = roundTripOptions)
        check ($"{optimization} exact input-copy and output-promotion capacities succeed") (
            exactRoundTrip.RetainedByteCount = outputBytes
            && exactRoundTrip.RetainedNodeCount = outputNodes
            && roundTripCapacity.GetProperty("scratchBytes").GetInt32() = inputBytes
            && roundTripCapacity.GetProperty("scratchNodes").GetInt32() = inputNodes
            && exactRoundTrip.StepsConsumed = roundTripCapacity.GetProperty("expectedSteps").GetInt32())
        check ($"{optimization} successful exact-capacity call reuses the same owner after language failure without decoding it") (
            exactRoundTrip.RetainedByteCount = roundTripCapacity.GetProperty("retainedBytes").GetInt32()
            && exactRoundTrip.RetainedNodeCount = roundTripCapacity.GetProperty("retainedNodes").GetInt32())

        let capacityFailure options =
            try
                let unexpected = roundTripNative.ExecuteRetainedWithInputs("state-round-trip", Some nativeInitial, roundTripArguments, options = options)
                (unexpected :> IDisposable).Dispose()
                failwith "Expected NativeResourceLimitException."
            with
            | :? NativeResourceLimitException as failure -> failure
        let shortScratchBytes = capacityFailure { roundTripOptions with ScratchByteCapacity = Some(inputBytes - 1) }
        check ($"{optimization} one-byte-short state import reports independent full-input totals") (
            shortScratchBytes.Code = "NATIVE_SCRATCH_CAPACITY"
            && shortScratchBytes.Arena = "scratch"
            && shortScratchBytes.RequiredBytes = int64 inputBytes
            && shortScratchBytes.RequiredNodes = int64 inputNodes
            && shortScratchBytes.AvailableBytes = inputBytes - 1
            && shortScratchBytes.AvailableNodes = inputNodes)
        let shortScratchNodes = capacityFailure { roundTripOptions with ScratchNodeCapacity = Some(inputNodes - 1) }
        check ($"{optimization} one-node-short state import reports independent full-input totals") (
            shortScratchNodes.Code = "NATIVE_SCRATCH_CAPACITY"
            && shortScratchNodes.Arena = "scratch"
            && shortScratchNodes.RequiredBytes = int64 inputBytes
            && shortScratchNodes.RequiredNodes = int64 inputNodes
            && shortScratchNodes.AvailableBytes = inputBytes
            && shortScratchNodes.AvailableNodes = inputNodes - 1)
        let shortRetainedBytes = capacityFailure { roundTripOptions with RetainedByteCapacity = Some(outputBytes - 1) }
        check ($"{optimization} one-byte-short reentry promotion preserves import inputs") (
            shortRetainedBytes.Code = "NATIVE_RETAINED_CAPACITY"
            && shortRetainedBytes.Arena = "retained"
            && shortRetainedBytes.RequiredBytes = int64 outputBytes
            && shortRetainedBytes.RequiredNodes = int64 outputNodes
            && shortRetainedBytes.AvailableBytes = outputBytes - 1
            && shortRetainedBytes.AvailableNodes = outputNodes)
        let shortRetainedNodes = capacityFailure { roundTripOptions with RetainedNodeCapacity = Some(outputNodes - 1) }
        check ($"{optimization} one-node-short reentry promotion reports independent live-output totals") (
            shortRetainedNodes.Code = "NATIVE_RETAINED_CAPACITY"
            && shortRetainedNodes.Arena = "retained"
            && shortRetainedNodes.RequiredBytes = int64 outputBytes
            && shortRetainedNodes.RequiredNodes = int64 outputNodes
            && shortRetainedNodes.AvailableBytes = outputBytes
            && shortRetainedNodes.AvailableNodes = outputNodes - 1)
        check ($"{optimization} failed calls leave the opaque input owner metadata unchanged") (
            nativeInitial.RetainedByteCount = inputBytes && nativeInitial.RetainedNodeCount = inputNodes)
        (validationNative :> IDisposable).Dispose()
        (mismatchNative :> IDisposable).Dispose()
        (roundTripNative :> IDisposable).Dispose()

        use nativeTurnOne = compileNative "state-turn-one" optimization turnBody
        use nativeTurnOneResult = nativeTurnOne.ExecuteRetainedWithInputs("state-turn", Some nativeInitial, turnOneArguments, options = turnOneOptions)
        check ($"{optimization} first native turn fuel and promotion totals match independent values") (
            nativeTurnOneResult.StepsConsumed = expectedTurnSteps
            && nativeTurnOneResult.StepsConsumed = coreTurnOneSteps
            && nativeTurnOneResult.RetainedByteCount = outputBytes
            && nativeTurnOneResult.RetainedNodeCount = outputNodes
            && nativeTurnOneResult.RetainedByteCount = int (turnOneFixture.GetProperty("retainedBytes").GetInt32())
            && nativeTurnOneResult.RetainedNodeCount = int (turnOneFixture.GetProperty("retainedNodes").GetInt32()))
        (nativeInitial :> IDisposable).Dispose()
        (nativeTurnOne :> IDisposable).Dispose()

        let recoveredValues = exactRoundTrip.Decode()
        check ($"{optimization} post-failure recovery bytes decode after the original owner and producer DLL are disposed") (
            recoveredValues = expectedRecoveryValues
            && formatValues recoveredValues = valuesInFixture (roundTripCapacity.GetProperty("values")))
        (exactRoundTrip :> IDisposable).Dispose()

        use nativeTurnTwo = compileNative "state-turn-two" optimization turnBody
        use nativeTurnTwoResult = nativeTurnTwo.ExecuteRetainedWithInputs("state-turn", Some nativeTurnOneResult, turnTwoArguments, options = turnTwoOptions)
        (nativeTurnOneResult :> IDisposable).Dispose()
        let nativeDisposedDiagnostic = captureNativeError nativeTurnTwo "state-turn" (Some nativeTurnOneResult) turnTwoArguments
        let disposedFixtureDiagnostic, _ = captureInterpreterError "state-turn" turnBody (Some coreTurnOne) turnTwoArguments
        check ($"{optimization} disposed native owner diagnostic matches interpreter") (nativeDisposedDiagnostic = disposedFixtureDiagnostic)
        assertDiagnosticFixture "disposed-owner" nativeDisposedDiagnostic
        check ($"{optimization} second native turn fuel and promotion totals match independent values") (
            nativeTurnTwoResult.StepsConsumed = expectedFinalSteps
            && nativeTurnTwoResult.StepsConsumed = coreTurnTwoSteps
            && nativeTurnTwoResult.RetainedByteCount = outputBytes
            && nativeTurnTwoResult.RetainedNodeCount = outputNodes
            && nativeTurnTwoResult.RetainedByteCount = int (turnTwoFixture.GetProperty("retainedBytes").GetInt32())
            && nativeTurnTwoResult.RetainedNodeCount = int (turnTwoFixture.GetProperty("retainedNodes").GetInt32()))
        (nativeTurnTwo :> IDisposable).Dispose()
        let finalNativeValues = nativeTurnTwoResult.Decode()
        check ($"{optimization} final native state decodes after prior owners and producer DLLs are disposed") (
            finalNativeValues = expectedTurnTwoValues
            && finalNativeValues = coreFinalValues
            && formatValues finalNativeValues = valuesInFixture (turnTwoFixture.GetProperty("values")))
        (nativeTurnTwoResult :> IDisposable).Dispose()

    use lockNative = compileNative "state-retained-lock" LlvmOptimization.O0 initializationBody
    use decodeOwner = lockNative.ExecuteRetained "state-init"
    use disposeOwner = lockNative.ExecuteRetained "state-init"
    (lockNative :> IDisposable).Dispose()

    let assertBorrowBlocksOperation (scenario: string) (owner: NativeRetainedResult) (operation: unit -> unit) =
        use borrowEntered = new ManualResetEventSlim(false)
        use releaseBorrow = new ManualResetEventSlim(false)
        use operationAttempted = new ManualResetEventSlim(false)
        let mutable borrowError: exn option = None
        let mutable operationError: exn option = None
        let borrower =
            Thread(ThreadStart(fun () ->
                try
                    owner.WithBorrow(verifiedProgram, "state-lock-test", fun _ _ _ ->
                        borrowEntered.Set()
                        if not (releaseBorrow.Wait(TimeSpan.FromSeconds 5.0)) then
                            raise (TimeoutException("The test did not release the retained-owner borrow.")))
                with error -> borrowError <- Some error))
        let contender =
            Thread(ThreadStart(fun () ->
                operationAttempted.Set()
                try operation ()
                with error -> operationError <- Some error))
        borrower.IsBackground <- true
        contender.IsBackground <- true
        borrower.Start()
        let entered = borrowEntered.Wait(TimeSpan.FromSeconds 5.0)
        contender.Start()
        let attempted = operationAttempted.Wait(TimeSpan.FromSeconds 5.0)
        let mutable observedLockWait = false
        if entered && attempted then
            let observationTimeout = Diagnostics.Stopwatch.StartNew()
            while not observedLockWait && observationTimeout.Elapsed < TimeSpan.FromSeconds 5.0 do
                observedLockWait <- contender.ThreadState.HasFlag(ThreadState.WaitSleepJoin)
        releaseBorrow.Set()
        let borrowerJoined = borrower.Join(TimeSpan.FromSeconds 5.0)
        let contenderJoined = contender.Join(TimeSpan.FromSeconds 5.0)
        check (scenario + " borrower enters WithBorrow") entered
        check (scenario + " contender starts while borrower owns the result lock") attempted
        check (scenario + " contender is observed waiting while the borrow is held") observedLockWait
        check (scenario + " borrower and contender finish after release") (borrowerJoined && contenderJoined)
        check (scenario + " borrow callback completes without error") borrowError.IsNone
        check (scenario + " contending operation completes without error") operationError.IsNone

    let mutable decodedDuringLockScenario: Value list option = None
    assertBorrowBlocksOperation "Decode" decodeOwner (fun () -> decodedDuringLockScenario <- Some(decodeOwner.Decode()))
    check "Decode completes with the retained values after the borrow releases" (decodedDuringLockScenario = Some expectedInitialValues)
    (decodeOwner :> IDisposable).Dispose()

    assertBorrowBlocksOperation "Dispose" disposeOwner (fun () -> (disposeOwner :> IDisposable).Dispose())
    let disposedAfterBorrow =
        errorOf (fun () ->
            disposeOwner.WithBorrow(verifiedProgram, "state-lock-test", fun _ _ _ -> ())
            |> ignore)
    check "Dispose completes after the borrow releases and closes the owner" (disposedAfterBorrow.Code = "IR_BACKEND_ENTRY_OWNER_DISPOSED")

let private testNominalValidatorFuelLimit () =
    let validatorInstructions =
        [ 1 .. 5000 ]
        |> List.collect (fun _ ->
            [ Push(LInt 1L, span "fuel-validator.agent" 3)
              Call("drop", span "fuel-validator.agent" 4) ])
    let validator =
        wordEntry "valid-after-many-drops?" [ TInt ] [ TBool ] Set.empty (
            validatorInstructions @ [
                Call("drop", span "fuel-validator.agent" 5)
                Push(LBool true, span "fuel-validator.agent" 6)
            ])
    let context =
        contextWithScalarDefinitions [ validator ] [
            scalarDefinition "FuelTag" TInt (Some "valid-after-many-drops?"), "FuelTag.make", "FuelTag.value"
        ]
    let body = compileBody context "validator-fuel-limit" [
        Push(LInt 1L, span "fuel-validator-main.agent" 1)
        Call("FuelTag.make", span "fuel-validator-main.agent" 2)
    ]
    let sources = NativeDiagnosticSources.fromLoweringContext context
    let mutable interpretedSteps = 0
    let host =
        { noOpHost () with
            ChargeInstruction = fun _ _ -> interpretedSteps <- interpretedSteps + 1
            WordDefinitionSpan = fun word -> sources.WordDefinitionSpans.TryFind word
            PrimitiveDefinitionSpan = fun word -> sources.PrimitiveDefinitionSpans.TryFind word }
    let interpretedError = errorOf (fun () -> IrInterpreter.executeBody host "validator-fuel-limit" body |> ignore)
    let expectedFixture = fixtureError "validator-fuel-limit"
    let expectedSpan = span "fuel-validator.agent" 3
    let expectedActual = expectedFixture.GetProperty("actual").EnumerateArray() |> Seq.map (fun value -> value.GetString()) |> Seq.toList
    check "validator fuel fixture independently specifies the first over-limit instruction" (
        interpretedError.Code = expectedFixture.GetProperty("code").GetString()
        && interpretedError.Word = Some(expectedFixture.GetProperty("word").GetString())
        && interpretedError.Actual = expectedActual
        && interpretedError.Span = Some expectedSpan
        && interpretedSteps = 10001)
    for optimization in [ LlvmOptimization.O0; LlvmOptimization.O2 ] do
        use native = compileNativeWithSources "validator-fuel-limit" optimization sources body
        let actualError = errorOf (fun () -> native.Execute "validator-fuel-limit" |> ignore)
        check ($"{optimization} validator fuel diagnostic matches interpreter") (actualError = interpretedError)
        let nativeStatus, nativeSteps = rawExecutionStatusAndSteps native
        check ($"{optimization} validator fuel guard reports a language diagnostic") (nativeStatus = NativeAbi.StatusDiagnostic)
        check ($"{optimization} validator fuel guard includes one Wrap and no generated-call charge") (nativeSteps = interpretedSteps)

let private testRejectsEffectsAndUnsupportedUntakenBranches () =
    let effectful =
        wordEntry "write" [ TString ] [ TUnit ] (Set.singleton "console.write")
            [ Call("console.write", span "write.agent" 4) ]
    let context = contextWith [ effectful ]
    let body =
        compileBody context "effect-branch"
            [ Push(LBool true, span "effect-main.agent" 1)
              If(
                  [ Push(LUnit, span "effect-main.agent" 2) ],
                  [ Push(LString "hidden", span "effect-main.agent" 3); Call("write", span "effect-main.agent" 4) ],
                  span "effect-main.agent" 5) ]
    let effectError = errorOf (fun () -> LlvmAot.emit body |> ignore)
    check "effects in an untaken reachable branch are rejected" (effectError.Code = "IR_LLVM_EFFECT_UNSUPPORTED")

    let ratioSpan = span "unsupported-ratio-primitive.agent" 4
    let ratioBody =
        compileBody (contextWith []) "unsupported-ratio-primitive"
            [ Push(LInt 7L, ratioSpan)
              Push(LInt 1L, ratioSpan)
              Push(LInt 2L, ratioSpan)
              Call("int.scale-ratio-toward-zero", ratioSpan)
              Call("drop", ratioSpan) ]
    let ratioError = errorOf (fun () -> LlvmAot.emit ratioBody |> ignore)
    check "LLVM rejects the ratio Result type at its explicit unsupported boundary"
        (ratioError.Code = "IR_LLVM_UNSUPPORTED_TYPE"
         && ratioError.Span = Some ratioSpan
         && ratioError.Actual = [ "Result<Int, String>" ])

    let enumSpan = span "unsupported-enum.agent" 1
    let enumDefinition =
        { Name = "NativeState"
          Cases = [ "active"; "cancelled" ]
          SourceText = "enum NativeState"
          Span = enumSpan }
    let enumContext = contextWithEnums [] [ enumDefinition ]
    let enumBody =
        compileBody enumContext "unsupported-enum"
            [ Call("NativeState.active", enumSpan)
              MatchEnum(
                  [ "active", [ Push(LInt 1L, enumSpan) ]
                    "cancelled", [ Push(LInt 0L, enumSpan) ] ],
                  enumSpan) ]
    let enumError = errorOf (fun () -> LlvmAot.emit enumBody |> ignore)
    check "LLVM refuses enum values and matches with its explicit unsupported diagnostic"
        (enumError.Code = "IR_LLVM_ENUM_UNSUPPORTED"
         && enumError.Actual = [ "NativeState" ])

    let unsupported =
        compileBody (contextWith []) "unsupported-branch"
            [ Push(LBool true, span "unsupported.agent" 1)
              If(
                  [ Push(LInt 1L, span "unsupported.agent" 2) ],
                  [ ConstructContainer(OptionNone, [ TInt ], span "unsupported.agent" 3)
                    MatchOption(
                        "value",
                        [ Load("value", span "unsupported.agent" 4) ],
                        [ Push(LInt 0L, span "unsupported.agent" 5) ],
                        span "unsupported.agent" 6) ],
                  span "unsupported.agent" 7) ]
    let operationError = errorOf (fun () -> LlvmAot.emit unsupported |> ignore)
    check "unsupported IR in an untaken branch is rejected before code generation" (
        operationError.Code = "IR_LLVM_UNSUPPORTED_OPERATION" && operationError.Span = Some(span "unsupported.agent" 3))

let private testCatalogTrustBoundary () =
    let model =
        { NominalTypesByKey = Map.empty
          FunctionsById = Map.empty
          GeneratedTargetsById = Map.empty
          SourceMap = Map.empty
          CoverageByWord = Map.empty }
        |> IrVerifier.verify Compiler.primitiveIrCatalog
    check "model-only verified program is not backend executable" (not (VerifiedIrProgram.isBackendExecutable model))
    let untrusted =
        try
            LlvmAot.validateProgram model
            false
        with
        | LanguageException diagnostic -> diagnostic.Code = "IR_BACKEND_UNTRUSTED_PROGRAM"
    check "LLVM backend rejects untrusted snapshots before execution" untrusted
    let trusted = Compiler.compileIrProgram (contextWith [])
    let wrongCatalog =
        try
            VerifiedIrProgram.requireBackendRegistry Map.empty trusted
            false
        with
        | LanguageException diagnostic -> diagnostic.Code = "IR_BACKEND_CATALOG_MISMATCH"
    check "canonical catalog mismatch is rejected" wrongCatalog

let private testCompilerRuntimeDiscovery () =
    let temporaryRoot = Path.Combine(Path.GetTempPath(), "agentlang-llvm-runtime-" + Guid.NewGuid().ToString("N"))
    let versionDirectory = Path.Combine(temporaryRoot, "MSVC", "99.0.0")
    let runtimeLibrary = Path.Combine(versionDirectory, "lib", "x64", "libcmt.lib")
    let previousVCTools = Environment.GetEnvironmentVariable("VCToolsInstallDir")
    let previousVsInstall = Environment.GetEnvironmentVariable("VSINSTALLDIR")
    let previousRuntime = Environment.GetEnvironmentVariable("AGENTLANG_COMPILER_RUNTIME_LIB")
    let restore name value = Environment.SetEnvironmentVariable(name, value)
    try
        Directory.CreateDirectory(Path.GetDirectoryName runtimeLibrary) |> ignore
        File.WriteAllText(runtimeLibrary, "temporary compiler-runtime discovery fixture")
        Environment.SetEnvironmentVariable("VCToolsInstallDir", versionDirectory + string Path.DirectorySeparatorChar)
        Environment.SetEnvironmentVariable("VSINSTALLDIR", null)
        Environment.SetEnvironmentVariable("AGENTLANG_COMPILER_RUNTIME_LIB", null)
        let fromVCTools = LlvmToolchain.discover().CompilerRuntimeLibrary
        check "runtime discovery accepts a trailing-separator VCToolsInstallDir version path" (
            fromVCTools = Some(Path.GetFullPath runtimeLibrary))

        let vsInstall = Path.Combine(temporaryRoot, "VisualStudio")
        let vsVersionDirectory = Path.Combine(vsInstall, "VC", "Tools", "MSVC", "88.0.0")
        let vsRuntimeLibrary = Path.Combine(vsVersionDirectory, "lib", "x64", "libcmt.lib")
        Directory.CreateDirectory(Path.GetDirectoryName vsRuntimeLibrary) |> ignore
        File.WriteAllText(vsRuntimeLibrary, "temporary compiler-runtime discovery fixture")
        Environment.SetEnvironmentVariable("VCToolsInstallDir", null)
        Environment.SetEnvironmentVariable("VSINSTALLDIR", vsInstall + string Path.DirectorySeparatorChar)
        let fromVsInstall = LlvmToolchain.discover().CompilerRuntimeLibrary
        check "runtime discovery expands VSINSTALLDIR to its MSVC toolset root" (
            fromVsInstall = Some(Path.GetFullPath vsRuntimeLibrary))
    finally
        restore "VCToolsInstallDir" previousVCTools
        restore "VSINSTALLDIR" previousVsInstall
        restore "AGENTLANG_COMPILER_RUNTIME_LIB" previousRuntime
        if Directory.Exists temporaryRoot then Directory.Delete(temporaryRoot, true)

[<EntryPoint>]
let main _ =
    try
        printStage "scalar operators and arithmetic"
        let boundary = compileBody (contextWith []) "signed-boundaries" [
            Push(LInt Int64.MinValue, span "boundary.agent" 1)
            Push(LInt 0L, span "boundary.agent" 2)
            Call("int.less-than", span "boundary.agent" 3)
            Push(LInt Int64.MaxValue, span "boundary.agent" 4)
            Push(LInt -1L, span "boundary.agent" 5)
            Call("int.greater-than", span "boundary.agent" 6)
        ]
        compareSuccessfulCase "signed-boundaries" "signed-boundaries" boundary [ BoolValue true; BoolValue true ]

        let signedTruncatingDivide = compileBody (contextWith []) "signed-truncating-divide" [
            Push(LInt -7L, span "signed-divide.agent" 1)
            Push(LInt 2L, span "signed-divide.agent" 2)
            Call("divide", span "signed-divide.agent" 3)
            Push(LInt 7L, span "signed-divide.agent" 4)
            Push(LInt -2L, span "signed-divide.agent" 5)
            Call("divide", span "signed-divide.agent" 6)
            Push(LInt -7L, span "signed-divide.agent" 7)
            Push(LInt -2L, span "signed-divide.agent" 8)
            Call("divide", span "signed-divide.agent" 9)
        ]
        compareSuccessfulCase "signed-truncating-divide" "signed-truncating-divide" signedTruncatingDivide [
            IntValue -3L; IntValue -3L; IntValue 3L
        ]

        let comparisonCases: (string * int64 * int64 * bool) list = [
            "int.less-than", 1L, 2L, true
            "int.less-than", 2L, 1L, false
            "int.greater-than", 2L, 1L, true
            "int.greater-than", 1L, 2L, false
            "int.less-or-equal", 2L, 2L, true
            "int.less-or-equal", 2L, 1L, false
            "int.greater-or-equal", 2L, 2L, true
            "int.greater-or-equal", 1L, 2L, false
        ]
        let comparisonExpressions =
            comparisonCases
            |> List.mapi (fun index (operation, left, right, _) ->
                let location = span "comparison.agent" (index + 1)
                [ Push(LInt left, location); Push(LInt right, location); Call(operation, location) ])
            |> List.concat
        let comparisonBody = compileBody (contextWith []) "comparison-outcomes" comparisonExpressions
        let comparisonExpected = comparisonCases |> List.map (fun (_, _, _, result) -> BoolValue result)
        compareSuccessfulCase "comparison-outcomes" "comparison-outcomes" comparisonBody comparisonExpected

        let booleanCases: (string * bool list * bool) list = [
            "bool.and", [ false; false ], false
            "bool.and", [ false; true ], false
            "bool.and", [ true; false ], false
            "bool.and", [ true; true ], true
            "bool.or", [ false; false ], false
            "bool.or", [ false; true ], true
            "bool.or", [ true; false ], true
            "bool.or", [ true; true ], true
            "bool.not", [ false ], true
            "bool.not", [ true ], false
        ]
        let booleanExpressions =
            booleanCases
            |> List.mapi (fun index (operation, inputs, _) ->
                let source = span "boolean-truth.agent" (index + 1)
                (inputs |> List.map (fun input -> Push(LBool input, source))) @ [ Call(operation, source) ])
            |> List.concat
        let booleanBody = compileBody (contextWith []) "boolean-truth-table" booleanExpressions
        let booleanExpected = booleanCases |> List.map (fun (_, _, result) -> BoolValue result)
        compareSuccessfulCase "boolean-truth-table" "boolean-truth-table" booleanBody booleanExpected

        let arithmeticAndBool = compileBody (contextWith []) "arithmetic-and-bool" [
            Push(LInt 9L, span "arithmetic.agent" 1)
            Push(LInt 2L, span "arithmetic.agent" 2)
            Call("subtract", span "arithmetic.agent" 3)
            Push(LInt 6L, span "arithmetic.agent" 4)
            Push(LInt 7L, span "arithmetic.agent" 5)
            Call("multiply", span "arithmetic.agent" 6)
            Push(LInt 9L, span "arithmetic.agent" 7)
            Push(LInt 3L, span "arithmetic.agent" 8)
            Call("divide", span "arithmetic.agent" 9)
            Push(LInt 1L, span "arithmetic.agent" 10)
            Push(LInt 2L, span "arithmetic.agent" 11)
            Call("int.less-than", span "arithmetic.agent" 12)
            Push(LInt 2L, span "arithmetic.agent" 13)
            Push(LInt 2L, span "arithmetic.agent" 14)
            Call("int.less-or-equal", span "arithmetic.agent" 15)
            Push(LInt 3L, span "arithmetic.agent" 16)
            Push(LInt 2L, span "arithmetic.agent" 17)
            Call("int.greater-or-equal", span "arithmetic.agent" 18)
            Push(LInt 42L, span "arithmetic.agent" 19)
            Push(LInt 42L, span "arithmetic.agent" 20)
            Call("equals", span "arithmetic.agent" 21)
            Push(LBool true, span "arithmetic.agent" 22)
            Push(LBool false, span "arithmetic.agent" 23)
            Call("equals", span "arithmetic.agent" 24)
            Push(LUnit, span "arithmetic.agent" 25)
            Push(LUnit, span "arithmetic.agent" 26)
            Call("equals", span "arithmetic.agent" 27)
            Push(LBool true, span "arithmetic.agent" 28)
            Push(LBool true, span "arithmetic.agent" 29)
            Call("bool.and", span "arithmetic.agent" 30)
            Push(LBool false, span "arithmetic.agent" 31)
            Push(LBool true, span "arithmetic.agent" 32)
            Call("bool.or", span "arithmetic.agent" 33)
            Push(LBool false, span "arithmetic.agent" 34)
            Call("bool.not", span "arithmetic.agent" 35)
            Push(LInt 5L, span "arithmetic.agent" 36)
            Push(LBool false, span "arithmetic.agent" 37)
            Call("swap", span "arithmetic.agent" 38)
        ]
        compareSuccessfulCase "arithmetic-and-bool" "arithmetic-and-bool" arithmeticAndBool [
            IntValue 7L; IntValue 42L; IntValue 3L
            BoolValue true; BoolValue true; BoolValue true
            BoolValue true; BoolValue false; BoolValue true
            BoolValue true; BoolValue true; BoolValue true
            BoolValue false; IntValue 5L
        ]

        let multiOutput = compileBody (contextWith []) "multi-output" [
            Push(LInt 42L, span "multi.agent" 1)
            Call("dup", span "multi.agent" 2)
            Push(LBool true, span "multi.agent" 3)
            Push(LUnit, span "multi.agent" 4)
        ]
        compareSuccessfulCase "multi-output" "multi-output" multiOutput [ IntValue 42L; IntValue 42L; BoolValue true; UnitValue ]

        let pairWord = wordEntry "pair-value" [] [ TInt; TBool ] Set.empty [
            Push(LInt 11L, span "pair.agent" 1)
            Push(LBool true, span "pair.agent" 2)
        ]
        let scratchBody = compileBody (contextWith [ pairWord ]) "call-output-scratch" [
            Call("pair-value", span "scratch.agent" 1)
            Call("drop", span "scratch.agent" 2)
        ]
        compareSuccessfulCase "call-output-scratch" "call-output-scratch" scratchBody [ IntValue 11L ]
        printStage "ABI layout and raw request guards"
        testLayoutAndRequestGuards multiOutput scratchBody

        printStage "branches, locals, and user-word calls"
        let branchJoin = compileBody (contextWith []) "branch-else-join" [
            Push(LInt 10L, span "branch.agent" 1)
            Let("saved", span "branch.agent" 2)
            Push(LBool false, span "branch.agent" 3)
            If(
                [ Push(LInt 99L, span "branch.agent" 4); Let("saved", span "branch.agent" 5); Load("saved", span "branch.agent" 6) ],
                [ Push(LInt 88L, span "branch.agent" 7); Let("saved", span "branch.agent" 8); Load("saved", span "branch.agent" 9) ],
                span "branch.agent" 3)
            Load("saved", span "branch.agent" 10)
        ]
        compareSuccessfulCase "branch-else-join" "branch-else-join" branchJoin [ IntValue 88L; IntValue 88L ]

        let branchThenJoin = compileBody (contextWith []) "branch-then-join" [
            Push(LInt 10L, span "branch-then.agent" 1)
            Let("saved", span "branch-then.agent" 2)
            Push(LBool true, span "branch-then.agent" 3)
            If(
                [ Push(LInt 99L, span "branch-then.agent" 4); Let("saved", span "branch-then.agent" 5); Load("saved", span "branch-then.agent" 6) ],
                [ Push(LInt 88L, span "branch-then.agent" 7); Let("saved", span "branch-then.agent" 8); Load("saved", span "branch-then.agent" 9) ],
                span "branch-then.agent" 3)
            Load("saved", span "branch-then.agent" 10)
        ]
        compareSuccessfulCase "branch-then-join" "branch-then-join" branchThenJoin [ IntValue 99L; IntValue 99L ]

        let scope = compileBody (contextWith []) "scope-restores-local" [
            Push(LInt 10L, span "scope.agent" 1)
            Let("saved", span "scope.agent" 2)
            Scope(
                [ Push(LInt 99L, span "scope.agent" 3)
                  Let("saved", span "scope.agent" 4)
                  Load("saved", span "scope.agent" 5) ],
                span "scope.agent" 6)
            Load("saved", span "scope.agent" 7)
        ]
        compareSuccessfulCase "scope-restores-local" "scope-restores-local" scope [ IntValue 99L; IntValue 10L ]

        let increment = wordEntry "increment" [ TInt ] [ TInt ] Set.empty [
            Push(LInt 1L, span "increment.agent" 1)
            Call("add", span "increment.agent" 2)
        ]
        let twice = wordEntry "twice" [ TInt ] [ TInt ] Set.empty [
            Call("increment", span "twice.agent" 1)
            Call("increment", span "twice.agent" 2)
        ]
        let userCalls = compileBody (contextWith [ increment; twice ]) "reachable-user-calls" [
            Push(LInt 17L, span "calls.agent" 1)
            Call("twice", span "calls.agent" 2)
        ]
        compareSuccessfulCase "reachable-user-calls" "reachable-user-calls" userCalls [ IntValue 19L ]

        let overflowContext = contextWith []
        let overflow = compileBody overflowContext "checked-overflow" [
            Push(LInt Int64.MaxValue, span "overflow.agent" 1)
            Push(LInt 1L, span "overflow.agent" 2)
            Call("add", span "overflow.agent" 3)
        ]
        compareErrorCase "checked-overflow" "overflow-execution" (NativeDiagnosticSources.fromLoweringContext overflowContext) overflow

        let divideZeroContext = contextWith []
        let divideZero = compileBody divideZeroContext "divide-by-zero" [
            Push(LInt 1L, span "divide.agent" 1)
            Push(LInt 0L, span "divide.agent" 2)
            Call("divide", span "divide.agent" 3)
        ]
        compareErrorCase "divide-by-zero" "divide-execution" (NativeDiagnosticSources.fromLoweringContext divideZeroContext) divideZero

        let subtractOverflowContext = contextWith []
        let subtractOverflow = compileBody subtractOverflowContext "subtract-overflow" [
            Push(LInt Int64.MinValue, span "subtract-overflow.agent" 1)
            Push(LInt 1L, span "subtract-overflow.agent" 2)
            Call("subtract", span "subtract-overflow.agent" 3)
        ]
        compareErrorCase "subtract-overflow" "subtract-overflow" (NativeDiagnosticSources.fromLoweringContext subtractOverflowContext) subtractOverflow

        let multiplyOverflowContext = contextWith []
        let multiplyOverflow = compileBody multiplyOverflowContext "multiply-overflow" [
            Push(LInt Int64.MaxValue, span "multiply-overflow.agent" 1)
            Push(LInt 2L, span "multiply-overflow.agent" 2)
            Call("multiply", span "multiply-overflow.agent" 3)
        ]
        compareErrorCase "multiply-overflow" "multiply-overflow" (NativeDiagnosticSources.fromLoweringContext multiplyOverflowContext) multiplyOverflow

        let divideOverflowContext = contextWith []
        let divideOverflow = compileBody divideOverflowContext "divide-overflow" [
            Push(LInt Int64.MinValue, span "divide-overflow.agent" 1)
            Push(LInt -1L, span "divide-overflow.agent" 2)
            Call("divide", span "divide-overflow.agent" 3)
        ]
        compareErrorCase "divide-overflow" "divide-overflow" (NativeDiagnosticSources.fromLoweringContext divideOverflowContext) divideOverflow

        let firstFailureContext = contextWith []
        let firstFailure = compileBody firstFailureContext "first-failure" [
            Push(LInt Int64.MaxValue, span "first-failure.agent" 1)
            Push(LInt 1L, span "first-failure.agent" 2)
            Call("add", span "first-failure.agent" 3)
            Push(LInt 1L, span "first-failure.agent" 4)
            Push(LInt 0L, span "first-failure.agent" 5)
            Call("divide", span "first-failure.agent" 6)
        ]
        compareErrorCase "first-failure" "first-failure" (NativeDiagnosticSources.fromLoweringContext firstFailureContext) firstFailure

        let aliasDefinition =
            wordEntry "add-alias" [ TInt; TInt ] [ TInt ] Set.empty []
            |> fun entry -> { entry with Builtin = Some(BuiltinOp "add"); Status = Primitive }
        let aliasContext = contextWith [ aliasDefinition ]
        let aliasOverflow = compileBody aliasContext "alias-overflow" [
            Push(LInt Int64.MaxValue, span "alias-overflow.agent" 1)
            Push(LInt 1L, span "alias-overflow.agent" 2)
            Call("add-alias", span "alias-overflow.agent" 3)
        ]
        compareErrorCase "alias-overflow" "alias-overflow" (NativeDiagnosticSources.fromLoweringContext aliasContext) aliasOverflow

        testFuelAndSourceMetadata ()
        testCallDepth ()
        testLargeStackFrame ()
        printStage "nominal scalar support and limits"
        testNominalScalarSupport ()
        testNominalScalarDiagnosticsAndRejections ()
        testNominalScalarDepth ()
        printStage "record construction, accessors, aliases, and equality"
        let sharingBody, sharingExpected = testRecordConformance ()
        printStage "record value depth and aggregate limits"
        testRecordValueMetrics ()
        printStage "record preflight diagnostics"
        testRecordPreflightRejections ()
        printStage "record capacity atomicity and retained ownership"
        testRetainedOwnershipAndCapacity sharingBody sharingExpected
        printStage "ABI v3 typed state re-entry, import budgets, and provenance"
        testTypedStateReentry ()
        testNominalValidatorFuelLimit ()
        testRejectsEffectsAndUnsupportedUntakenBranches ()
        testCatalogTrustBoundary ()
        testCompilerRuntimeDiscovery ()
        printfn "AgentLang.Llvm.Tests: %d assertions passed; artifacts: %s" assertions artifactRoot
        0
    with ex ->
        eprintfn "%s" (ex.ToString())
        1
