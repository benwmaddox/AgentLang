module AgentLang.Llvm.Tests

open System
open System.IO
open System.Runtime.InteropServices
open System.Text
open System.Text.Json
open System.Threading
open AgentLang
open AgentLang.Llvm

[<UnmanagedFunctionPointer(CallingConvention.Cdecl)>]
type private RawExecuteDelegate = delegate of nativeint * nativeint * int32 * nativeint -> unit

[<UnmanagedFunctionPointer(CallingConvention.Cdecl)>]
type private RawOwningExecuteDelegate = delegate of nativeint * nativeint * int32 * nativeint * int32 * nativeint * int32 -> int32

[<UnmanagedFunctionPointer(CallingConvention.Cdecl)>]
type private OwningMailboxModuleExport = delegate of unit -> nativeint

[<UnmanagedFunctionPointer(CallingConvention.Cdecl)>]
type private OwningMailboxEntryDelegate = delegate of nativeint * nativeint * uint32 * nativeint * uint32 -> int32

[<UnmanagedFunctionPointer(CallingConvention.Cdecl)>]
type private OwningMailboxAssociatedResumeDelegate = delegate of nativeint * nativeint * uint32 * nativeint * uint32 * nativeint * uint32 -> int32

[<Struct; StructLayout(LayoutKind.Sequential, Pack = 8)>]
type private RawMailboxExternalSlice =
    val mutable Bytes: nativeint
    val mutable ExtentBytes: uint32
    val mutable TypeIndex: uint32

[<Struct; StructLayout(LayoutKind.Sequential, Pack = 8)>]
type private RawMailboxOutputSlice =
    val mutable TypeIndex: uint32
    val mutable OffsetBytes: uint32
    val mutable OwnerEndBytes: uint32
    val mutable Reserved: uint32

[<UnmanagedFunctionPointer(CallingConvention.Cdecl)>]
type private LayoutDelegate = delegate of nativeint -> unit

[<UnmanagedFunctionPointer(CallingConvention.Cdecl)>]
type private CapacityDelegate = delegate of unit -> int32

[<UnmanagedFunctionPointer(CallingConvention.Cdecl)>]
type private ModuleDescriptorDelegate = delegate of unit -> nativeint

[<Struct; StructLayout(LayoutKind.Sequential, Pack = 8)>]
type private RawModuleDiagnostic =
    val mutable Id: int32
    val mutable Line: uint32
    val mutable Column: uint32
    val mutable Length: uint32
    val mutable Code: nativeint
    val mutable Word: nativeint
    val mutable File: nativeint

[<Struct; StructLayout(LayoutKind.Sequential, Pack = 8)>]
type private RawModuleEntry =
    val mutable StructSize: uint32
    val mutable EntryId: uint32
    val mutable Name: nativeint
    val mutable Execute: nativeint
    val mutable InputCount: uint32
    val mutable OutputCount: uint32
    val mutable WorkspaceCapacity: uint32
    val mutable DiagnosticCount: uint32
    val mutable InputTypeIds: nativeint
    val mutable OutputTypeIds: nativeint
    val mutable Diagnostics: nativeint

[<Struct; StructLayout(LayoutKind.Sequential, Pack = 8)>]
type private RawModuleDescriptor =
    val mutable AbiVersion: uint32
    val mutable StructSize: uint32
    val mutable RuntimeAbiVersion: uint32
    val mutable EntryCount: uint32
    val mutable Fingerprint0: uint64
    val mutable Fingerprint1: uint64
    val mutable Fingerprint2: uint64
    val mutable Fingerprint3: uint64
    val mutable Program: nativeint
    val mutable Entries: nativeint
    val mutable TypeNames: nativeint

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

let private compileBodyWithInputs context name inputTypes expressions =
    let verifiedProgram = Compiler.compileIrProgram context
    Compiler.compileIrBodyAgainstProgram context verifiedProgram name inputTypes expressions

let private noOpHost () : IrInterpreterHost =
    { PreflightEffects = fun _ _ _ -> ()
      ChargeInstruction = fun _ _ -> ()
      RecordBranchOutcome = fun _ _ _ -> ()
      RecordUse = ignore
      InvokeEffect = fun _ -> EffectUnit
      EnterUserFunction = fun _ _ _ -> fun () -> ()
      ReturnUserFunction = fun _ _ _ -> ()
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
    let arguments = Environment.GetCommandLineArgs()
    match arguments |> Array.tryFindIndex ((=) "--artifacts-path") with
    | Some index when index + 1 < arguments.Length -> Path.GetFullPath(arguments[index + 1])
    | Some _ -> invalidArg "--artifacts-path" "The artifact directory must follow --artifacts-path."
    | None ->
        let run = Guid.NewGuid().ToString("N")
        Path.Combine(Directory.GetCurrentDirectory(), ".agentlang", "native-validation", "tests-" + run)

let private compileNative (name: string) (optimization: LlvmOptimization) (body: VerifiedIrBody) =
    let directory = Path.Combine(artifactRoot, name, string optimization)
    LlvmAot.compile (LlvmToolchain.discover()) optimization directory NativeDiagnosticSources.empty body

let private compileNativeWithSources (name: string) (optimization: LlvmOptimization) (sources: NativeDiagnosticSources) (body: VerifiedIrBody) =
    let directory = Path.Combine(artifactRoot, name, string optimization)
    LlvmAot.compile (LlvmToolchain.discover()) optimization directory sources body

let private compileOwningNative toolchain (name: string) (optimization: LlvmOptimization) (body: VerifiedIrBody) =
    let directory = Path.Combine(artifactRoot, name, string optimization)
    OwningStackAot.compile toolchain optimization directory body

type private RawMailboxInvocation =
    { Status: int32
      Context: NativeOwningContext
      Outputs: RawMailboxOutputSlice list
      RetainedInputs: RawMailboxOutputSlice list
      StackBytes: byte array
      InitializedBitmap: byte array
      PoisonBitmap: byte array
      TraceBytes: byte array
      Diagnostic: OwningMailboxDiagnosticInfo option }

let private mailboxIntBytes (value: int64) = BitConverter.GetBytes value

let private mailboxStringBytes (value: string) =
    let utf16 = Encoding.Unicode.GetBytes value
    let payloadBytes = 8 + utf16.Length
    let extentBytes = (payloadBytes + 7) &&& ~~~7
    let bytes = Array.zeroCreate<byte> extentBytes
    Buffer.BlockCopy(BitConverter.GetBytes(value.Length), 0, bytes, 0, sizeof<int32>)
    Buffer.BlockCopy(utf16, 0, bytes, 8, utf16.Length)
    bytes

let private invokeRawMailboxEntry (native: OwningMailboxCompiledModule) entryIndex (inputs: byte array list) =
    if entryIndex < 0 || entryIndex >= native.Entries.Length then invalidArg (nameof entryIndex) "Mailbox entry index is outside the compiled module."
    let entry = native.Entries[entryIndex]
    if inputs.Length <> entry.InputTypes.Length then invalidArg (nameof inputs) "Mailbox input bytes do not match the compiled entry shape."
    let library = NativeLibrary.Load native.LibraryPath
    let inputBuffers = Array.zeroCreate<nativeint> inputs.Length
    let inputDescriptors = Marshal.AllocHGlobal(max 1 (inputs.Length * Marshal.SizeOf<RawMailboxExternalSlice>()))
    let outputCapacity = entry.OutputTypes.Length
    let outputDescriptors = Marshal.AllocHGlobal(max 1 (outputCapacity * Marshal.SizeOf<RawMailboxOutputSlice>()))
    let contextPointer = Marshal.AllocHGlobal(Marshal.SizeOf<NativeOwningContext>())
    let stackCapacity = 4096
    let bitmapBytes = stackCapacity / 8
    let stack = Marshal.AllocHGlobal stackCapacity
    let initialized = Marshal.AllocHGlobal bitmapBytes
    let poison = Marshal.AllocHGlobal bitmapBytes
    let traceCapacity = 8192
    let trace = Marshal.AllocHGlobal(traceCapacity * 40)
    try
        for index, bytes in inputs |> List.indexed do
            let buffer = Marshal.AllocHGlobal(max 1 bytes.Length)
            inputBuffers[index] <- buffer
            if bytes.Length > 0 then Marshal.Copy(bytes, 0, buffer, bytes.Length)
            let mutable descriptor = Unchecked.defaultof<RawMailboxExternalSlice>
            descriptor.Bytes <- buffer
            descriptor.ExtentBytes <- uint32 bytes.Length
            descriptor.TypeIndex <- entry.InputTypeIndexes[index]
            Marshal.StructureToPtr(descriptor, IntPtr.Add(inputDescriptors, index * Marshal.SizeOf<RawMailboxExternalSlice>()), false)
        let outputBytes = max 1 (outputCapacity * Marshal.SizeOf<RawMailboxOutputSlice>())
        Marshal.Copy(Array.create outputBytes 0xA5uy, 0, outputDescriptors, outputBytes)
        Marshal.Copy(Array.zeroCreate<byte> stackCapacity, 0, stack, stackCapacity)
        Marshal.Copy(Array.zeroCreate<byte> bitmapBytes, 0, initialized, bitmapBytes)
        Marshal.Copy(Array.zeroCreate<byte> bitmapBytes, 0, poison, bitmapBytes)
        Marshal.Copy(Array.zeroCreate<byte> (traceCapacity * 40), 0, trace, traceCapacity * 40)
        let mutable context = Unchecked.defaultof<NativeOwningContext>
        context.AbiVersion <- 1u
        context.StackCapacityBytes <- uint32 stackCapacity
        context.AvailableBytes <- uint32 stackCapacity
        context.TraceEventCapacity <- uint32 traceCapacity
        context.InitBitmapBytes <- uint32 bitmapBytes
        context.StackData <- stack
        context.InitBitmap <- initialized
        context.PoisonBitmap <- poison
        context.TraceEvents <- trace
        Marshal.StructureToPtr(context, contextPointer, false)
        let export = Marshal.GetDelegateForFunctionPointer<OwningMailboxModuleExport>(NativeLibrary.GetExport(library, "agentlang_owning_mailbox_module"))
        let modulePointer = export.Invoke()
        let executePointer = Marshal.ReadIntPtr(modulePointer, 16 + entryIndex * 40 + 32)
        let execute = Marshal.GetDelegateForFunctionPointer<OwningMailboxEntryDelegate>(executePointer)
        let status = execute.Invoke(contextPointer, inputDescriptors, uint32 inputs.Length, outputDescriptors, uint32 outputCapacity)
        let finalContext = Marshal.PtrToStructure<NativeOwningContext>(contextPointer)
        let stackBytes = Array.zeroCreate<byte> stackCapacity
        Marshal.Copy(stack, stackBytes, 0, stackCapacity)
        let initializedBytes = Array.zeroCreate<byte> bitmapBytes
        Marshal.Copy(initialized, initializedBytes, 0, bitmapBytes)
        let poisonBytes = Array.zeroCreate<byte> bitmapBytes
        Marshal.Copy(poison, poisonBytes, 0, bitmapBytes)
        let traceBytes = Array.zeroCreate<byte> (traceCapacity * 40)
        Marshal.Copy(trace, traceBytes, 0, traceBytes.Length)
        let outputSize = Marshal.SizeOf<RawMailboxOutputSlice>()
        let outputValues =
            [ for index in 0 .. outputCapacity - 1 do
                yield Marshal.PtrToStructure<RawMailboxOutputSlice>(IntPtr.Add(outputDescriptors, index * outputSize)) ]
        let diagnostic = native.Diagnostics |> List.tryFind (fun item -> uint32 item.Id = finalContext.ErrorId)
        { Status = status
          Context = finalContext
          Outputs = outputValues
          RetainedInputs = []
          StackBytes = stackBytes
          InitializedBitmap = initializedBytes
          PoisonBitmap = poisonBytes
          TraceBytes = traceBytes
          Diagnostic = diagnostic }
    finally
        NativeLibrary.Free library
        Marshal.FreeHGlobal trace
        Marshal.FreeHGlobal poison
        Marshal.FreeHGlobal initialized
        Marshal.FreeHGlobal stack
        Marshal.FreeHGlobal contextPointer
        Marshal.FreeHGlobal outputDescriptors
        Marshal.FreeHGlobal inputDescriptors
        for buffer in inputBuffers do
            if buffer <> IntPtr.Zero then Marshal.FreeHGlobal buffer

let private invokeRawMailboxAssociatedResumeWithRetry
    (native: OwningMailboxCompiledModule)
    (parkedLease: RawMailboxInvocation)
    (invalidRetainedState: byte array)
    (validButSemanticallyInvalidRetainedState: byte array)
    (completionBytes: byte array) =
    if invalidRetainedState.Length <> 8 || validButSemanticallyInvalidRetainedState.Length <> 8 then
        invalidArg (nameof invalidRetainedState) "The focused associated-resume fixture expects an eight-byte Bool-backed field."
    if parkedLease.Outputs.Length <> 2 || parkedLease.Context.CallDepth <> 0u then
        invalidArg (nameof parkedLease) "The associated-resume fixture requires a completed begin lease with two parked roots."
    let library = NativeLibrary.Load native.LibraryPath
    let inputBuffer = Marshal.AllocHGlobal(max 1 completionBytes.Length)
    let completionDescriptor = Marshal.AllocHGlobal(Marshal.SizeOf<RawMailboxExternalSlice>())
    let retainedDescriptors = Marshal.AllocHGlobal(2 * Marshal.SizeOf<RawMailboxOutputSlice>())
    let outputDescriptor = Marshal.AllocHGlobal(Marshal.SizeOf<RawMailboxOutputSlice>())
    let contextPointer = Marshal.AllocHGlobal(Marshal.SizeOf<NativeOwningContext>())
    let stackCapacity = int parkedLease.Context.StackCapacityBytes
    let bitmapBytes = parkedLease.InitializedBitmap.Length
    let traceBytes = parkedLease.TraceBytes.Length
    let traceCapacity = traceBytes / 40
    let stack = Marshal.AllocHGlobal stackCapacity
    let initialized = Marshal.AllocHGlobal bitmapBytes
    let poison = Marshal.AllocHGlobal bitmapBytes
    let trace = Marshal.AllocHGlobal(traceBytes)
    try
        Marshal.Copy(completionBytes, 0, inputBuffer, completionBytes.Length)
        let mutable completion = Unchecked.defaultof<RawMailboxExternalSlice>
        completion.Bytes <- inputBuffer
        completion.ExtentBytes <- uint32 completionBytes.Length
        completion.TypeIndex <- native.Entries[2].InputTypeIndexes[2]
        Marshal.StructureToPtr(completion, completionDescriptor, false)

        for index, descriptor in parkedLease.Outputs |> List.indexed do
            Marshal.StructureToPtr(descriptor, IntPtr.Add(retainedDescriptors, index * Marshal.SizeOf<RawMailboxOutputSlice>()), false)

        let stateLayout =
            native.Layouts
            |> List.find (fun layout -> layout.Type = native.Entries[2].InputTypes[0])
        let stateFieldOffset = parkedLease.Outputs[0].OffsetBytes + uint32 stateLayout.Fields.Head.OffsetBytes
        let parkedCursor = parkedLease.Context.CursorBytes
        if uint64 parkedCursor + uint64 completionBytes.Length > uint64 stackCapacity then
            invalidArg (nameof parkedLease) "The completed begin lease has no room for the focused completion sentinel."
        Marshal.Copy(Array.create (Marshal.SizeOf<RawMailboxOutputSlice>()) 0xA5uy, 0, outputDescriptor, Marshal.SizeOf<RawMailboxOutputSlice>())
        let initialStack = Array.copy parkedLease.StackBytes
        Array.Copy(invalidRetainedState, 0, initialStack, int stateFieldOffset, invalidRetainedState.Length)
        Array.Fill(initialStack, 0xCDuy, int parkedCursor, completionBytes.Length)
        Marshal.Copy(initialStack, 0, stack, stackCapacity)
        Marshal.Copy(parkedLease.InitializedBitmap, 0, initialized, bitmapBytes)
        Marshal.Copy(parkedLease.PoisonBitmap, 0, poison, bitmapBytes)
        Marshal.Copy(parkedLease.TraceBytes, 0, trace, traceBytes)
        let mutable context = parkedLease.Context
        context.StackCapacityBytes <- uint32 stackCapacity
        context.TraceEventCapacity <- uint32 traceCapacity
        context.InitBitmapBytes <- uint32 bitmapBytes
        context.AvailableBytes <- uint32 stackCapacity - context.CursorBytes
        context.StackData <- stack
        context.InitBitmap <- initialized
        context.PoisonBitmap <- poison
        context.TraceEvents <- trace
        Marshal.StructureToPtr(context, contextPointer, false)
        let export = Marshal.GetDelegateForFunctionPointer<OwningMailboxModuleExport>(NativeLibrary.GetExport(library, "agentlang_owning_mailbox_module"))
        let modulePointer = export.Invoke()
        // ABI v1 places three fixed-size entry records before the associated callback pointer.
        let associatedPointer = Marshal.ReadIntPtr(modulePointer, 16 + 3 * 40)
        let associated = Marshal.GetDelegateForFunctionPointer<OwningMailboxAssociatedResumeDelegate>(associatedPointer)
        let capture status =
            let finalContext = Marshal.PtrToStructure<NativeOwningContext>(contextPointer)
            let stackBytes = Array.zeroCreate<byte> stackCapacity
            Marshal.Copy(stack, stackBytes, 0, stackCapacity)
            let initializedBytes = Array.zeroCreate<byte> bitmapBytes
            Marshal.Copy(initialized, initializedBytes, 0, bitmapBytes)
            let poisonBytes = Array.zeroCreate<byte> bitmapBytes
            Marshal.Copy(poison, poisonBytes, 0, bitmapBytes)
            let traceBytes = Array.zeroCreate<byte> (traceCapacity * 40)
            Marshal.Copy(trace, traceBytes, 0, traceBytes.Length)
            let output = Marshal.PtrToStructure<RawMailboxOutputSlice>(outputDescriptor)
            let retained = [
                Marshal.PtrToStructure<RawMailboxOutputSlice>(retainedDescriptors)
                Marshal.PtrToStructure<RawMailboxOutputSlice>(IntPtr.Add(retainedDescriptors, Marshal.SizeOf<RawMailboxOutputSlice>()))
            ]
            let diagnostic = native.Diagnostics |> List.tryFind (fun item -> uint32 item.Id = finalContext.ErrorId)
            { Status = status
              Context = finalContext
              Outputs = [ output ]
              RetainedInputs = retained
              StackBytes = stackBytes
              InitializedBitmap = initializedBytes
              PoisonBitmap = poisonBytes
              TraceBytes = traceBytes
              Diagnostic = diagnostic }
        let firstStatus = associated.Invoke(contextPointer, retainedDescriptors, 2u, completionDescriptor, parkedCursor, outputDescriptor, 1u)
        let first = capture firstStatus

        // Retry the same parked lease after correcting only the raw Bool bytes.
        // False is canonical but fails TrueTag's frozen validator, so success
        // also demonstrates that KEEP does not replay semantic validation.
        Marshal.Copy(validButSemanticallyInvalidRetainedState, 0, IntPtr.Add(stack, int stateFieldOffset), validButSemanticallyInvalidRetainedState.Length)
        let mutable retryContext = Marshal.PtrToStructure<NativeOwningContext>(contextPointer)
        retryContext.Status <- 0u
        retryContext.ErrorId <- 0u
        retryContext.RequiredBytes <- 0u
        retryContext.AvailableBytes <- uint32 stackCapacity - retryContext.CursorBytes
        Marshal.StructureToPtr(retryContext, contextPointer, false)
        Marshal.Copy(Array.create (Marshal.SizeOf<RawMailboxOutputSlice>()) 0xA5uy, 0, outputDescriptor, Marshal.SizeOf<RawMailboxOutputSlice>())
        let retryStatus = associated.Invoke(contextPointer, retainedDescriptors, 2u, completionDescriptor, parkedCursor, outputDescriptor, 1u)
        first, capture retryStatus
    finally
        NativeLibrary.Free library
        Marshal.FreeHGlobal trace
        Marshal.FreeHGlobal poison
        Marshal.FreeHGlobal initialized
        Marshal.FreeHGlobal stack
        Marshal.FreeHGlobal contextPointer
        Marshal.FreeHGlobal outputDescriptor
        Marshal.FreeHGlobal retainedDescriptors
        Marshal.FreeHGlobal completionDescriptor
        Marshal.FreeHGlobal inputBuffer

let private invokeRawOwningEntryForMetrics
    (native: OwningStackCompiledProgram)
    (inputBytes: byte array)
    (inputExtents: int array) =
    let library = NativeLibrary.Load native.LibraryPath
    let input = Marshal.AllocHGlobal(max 1 inputBytes.Length)
    let extents = Marshal.AllocHGlobal(max sizeof<int32> (inputExtents.Length * sizeof<int32>))
    let retainedCapacity = 4096
    let retained = Marshal.AllocHGlobal(retainedCapacity)
    let contextPointer = Marshal.AllocHGlobal(Marshal.SizeOf<NativeOwningContext>())
    let stackCapacity = 4096
    let bitmapBytes = stackCapacity / 8
    let stack = Marshal.AllocHGlobal stackCapacity
    let initialized = Marshal.AllocHGlobal bitmapBytes
    let poison = Marshal.AllocHGlobal bitmapBytes
    let traceCapacity = 8192
    let trace = Marshal.AllocHGlobal(traceCapacity * 40)
    try
        if inputBytes.Length > 0 then Marshal.Copy(inputBytes, 0, input, inputBytes.Length)
        if inputExtents.Length > 0 then
            Marshal.Copy(Array.zeroCreate<byte> (inputExtents.Length * sizeof<int32>), 0, extents, inputExtents.Length * sizeof<int32>)
        for index, extent in inputExtents |> Array.toList |> List.indexed do
            Marshal.WriteInt32(extents, index * sizeof<int32>, extent)
        Marshal.Copy(Array.create retainedCapacity 0xA5uy, 0, retained, retainedCapacity)
        Marshal.Copy(Array.zeroCreate<byte> stackCapacity, 0, stack, stackCapacity)
        Marshal.Copy(Array.zeroCreate<byte> bitmapBytes, 0, initialized, bitmapBytes)
        Marshal.Copy(Array.zeroCreate<byte> bitmapBytes, 0, poison, bitmapBytes)
        Marshal.Copy(Array.zeroCreate<byte> (traceCapacity * 40), 0, trace, traceCapacity * 40)
        let mutable context = Unchecked.defaultof<NativeOwningContext>
        context.AbiVersion <- 1u
        context.StackCapacityBytes <- uint32 stackCapacity
        context.TraceEventCapacity <- uint32 traceCapacity
        context.InitBitmapBytes <- uint32 bitmapBytes
        context.StackData <- stack
        context.InitBitmap <- initialized
        context.PoisonBitmap <- poison
        context.TraceEvents <- trace
        Marshal.StructureToPtr(context, contextPointer, false)
        let execute = Marshal.GetDelegateForFunctionPointer<RawOwningExecuteDelegate>(NativeLibrary.GetExport(library, "agentlang_owning_execute"))
        let status = execute.Invoke(contextPointer, input, inputBytes.Length, extents, inputExtents.Length, retained, retainedCapacity)
        let finalContext = Marshal.PtrToStructure<NativeOwningContext>(contextPointer)
        let retainedBytes = Array.zeroCreate<byte> retainedCapacity
        Marshal.Copy(retained, retainedBytes, 0, retainedCapacity)
        status, finalContext, retainedBytes
    finally
        NativeLibrary.Free library
        Marshal.FreeHGlobal trace
        Marshal.FreeHGlobal poison
        Marshal.FreeHGlobal initialized
        Marshal.FreeHGlobal stack
        Marshal.FreeHGlobal contextPointer
        Marshal.FreeHGlobal retained
        Marshal.FreeHGlobal extents
        Marshal.FreeHGlobal input

let private interpreterResultWithInputsAndSteps (executionName: string) (body: VerifiedIrBody) (arguments: IrEntryArgument list) =
    let mutable steps = 0
    let host = { noOpHost () with ChargeInstruction = fun _ _ -> steps <- steps + 1 }
    use result = IrInterpreter.executeBodyWithInputs host executionName body None arguments
    result.Decode(), steps

let private interpreterResultWithOwnerAndSteps
    (executionName: string)
    (body: VerifiedIrBody)
    (inputOwner: IrInterpreterResult option)
    (arguments: IrEntryArgument list) =
    let mutable steps = 0
    let host = { noOpHost () with ChargeInstruction = fun _ _ -> steps <- steps + 1 }
    use result = IrInterpreter.executeBodyWithInputs host executionName body inputOwner arguments
    result.Decode(), steps

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

let private invokeRawModuleEntry (entryPointer: nativeint) (inputValues: int64 array) (inputTypeIds: uint32 array) (outputCount: int) (workspaceCapacity: int) =
    if inputValues.Length <> inputTypeIds.Length then invalidArg (nameof inputTypeIds) "Input values and type IDs must have equal lengths."
    let execute = Marshal.GetDelegateForFunctionPointer<RawExecuteDelegate>(entryPointer)
    let scratch = allocateRawArena 1_000_000 10_000 7
    let retained = allocateRawArena 1_000_000 10_000 11
    let context = Marshal.AllocHGlobal NativeAbi.ContextSize
    let outputs = Marshal.AllocHGlobal(max NativeAbi.SlotSize (outputCount * NativeAbi.SlotSize))
    let workspace = Marshal.AllocHGlobal(max NativeAbi.SlotSize (workspaceCapacity * NativeAbi.SlotSize))
    let inputRoots = if inputValues.Length = 0 then nativeint 0 else Marshal.AllocHGlobal(inputValues.Length * NativeAbi.SlotSize)
    let inputTypes = if inputTypeIds.Length = 0 then nativeint 0 else Marshal.AllocHGlobal(inputTypeIds.Length * sizeof<uint32>)
    let status = Marshal.AllocHGlobal sizeof<int32>
    try
        Marshal.Copy(Array.zeroCreate<byte> NativeAbi.ContextSize, 0, context, NativeAbi.ContextSize)
        Marshal.WriteInt32(context, NativeAbi.ContextAbiVersionOffset, int NativeAbi.Version)
        Marshal.WriteInt32(context, NativeAbi.ContextErrorMetadataIdOffset, -1)
        Marshal.WriteIntPtr(context, NativeAbi.ContextScratchOffset, nativeint (let descriptor, _, _ = scratch in descriptor))
        Marshal.WriteIntPtr(context, NativeAbi.ContextRetainedOffset, nativeint (let descriptor, _, _ = retained in descriptor))
        Marshal.WriteIntPtr(context, NativeAbi.ContextWorkspaceOffset, nativeint workspace)
        Marshal.WriteInt32(context, NativeAbi.ContextWorkspaceCapacityOffset, workspaceCapacity)
        Marshal.WriteIntPtr(context, NativeAbi.ContextInputRootsOffset, inputRoots)
        Marshal.WriteIntPtr(context, NativeAbi.ContextInputRootTypeIdsOffset, inputTypes)
        Marshal.WriteInt32(context, NativeAbi.ContextInputRootCountOffset, inputValues.Length)
        for index in 0 .. inputValues.Length - 1 do
            Marshal.WriteInt64(inputRoots, index * NativeAbi.SlotSize, inputValues[index])
            Marshal.WriteInt32(inputTypes, index * sizeof<uint32>, int inputTypeIds[index])
        Marshal.WriteInt32(status, NativeAbi.StatusInvalidRequest)
        execute.Invoke(nativeint context, nativeint outputs, outputCount, nativeint status)
        let statusValue = Marshal.ReadInt32 status
        let steps = Marshal.ReadInt32(context, NativeAbi.ContextStepsConsumedOffset)
        let values = [ for index in 0 .. outputCount - 1 -> Marshal.ReadInt64(outputs, index * NativeAbi.SlotSize) ]
        statusValue, steps, values
    finally
        Marshal.FreeHGlobal status
        if inputTypes <> nativeint 0 then Marshal.FreeHGlobal inputTypes
        if inputRoots <> nativeint 0 then Marshal.FreeHGlobal inputRoots
        Marshal.FreeHGlobal workspace
        Marshal.FreeHGlobal outputs
        Marshal.FreeHGlobal context
        freeRawArena retained
        freeRawArena scratch

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
      Validator = None
      SourceText = "record " + name
      Span = span (name + ".agent") 1 }

let private contextWithRecordDefinitions extraWords (records: RecordDefinition list) scalarDefinitions =
    let recordMap = records |> List.map (fun record -> record.Name, record) |> Map.ofList
    contextWithDefinitions extraWords recordMap scalarDefinitions

let private testOwningNominalIntSlice () =
    let scalarContext =
        contextWithScalarDefinitions [] [
            scalarDefinition "Meters" TInt None, "Meters.create", "Meters.raw"
            scalarDefinition "OrderId" TInt None, "OrderId.create", "OrderId.raw"
        ]
    let metersType = IrNominal(ProgramTypeKey 0)
    let orderIdType = IrNominal(ProgramTypeKey 1)
    let identityBody = compileBodyWithInputs scalarContext "owning-nominal-identity" [ TNamed "Meters"; TNamed "OrderId" ] []
    let wrapUnwrapBody = compileBodyWithInputs scalarContext "owning-nominal-wrap-unwrap" [ TInt ] [
        Call("Meters.create", span "owning-nominal.agent" 1)
        Call("Meters.raw", span "owning-nominal.agent" 2)
    ]
    let toolchain = LlvmToolchain.discover()
    let fixtures = [
        "zero", 0L, "0000000000000000"
        "negative", -42L, "d6ffffffffffffff"
        "minimum", Int64.MinValue, "0000000000000080"
        "maximum", Int64.MaxValue, "ffffffffffffff7f"
    ]

    for optimization in [ LlvmOptimization.O0; LlvmOptimization.O2 ] do
        use identity = compileOwningNative toolchain "owning-nominal-identity" optimization identityBody
        let metersLayout = identity.Layouts |> List.find (fun layout -> layout.TypeName = "Meters")
        let orderIdLayout = identity.Layouts |> List.find (fun layout -> layout.TypeName = "OrderId")
        check ($"{optimization} Meters retains nominal key and fixed Int layout") (
            metersLayout.Type = metersType
            && metersLayout.PayloadBytes = 8 && metersLayout.ExtentBytes = 8
            && metersLayout.MinimumPayloadBytes = 8 && metersLayout.MinimumExtentBytes = 8
            && not metersLayout.IsDynamic)
        check ($"{optimization} OrderId retains a distinct nominal key and fixed Int layout") (
            orderIdLayout.Type = orderIdType
            && orderIdLayout.PayloadBytes = 8 && orderIdLayout.ExtentBytes = 8
            && orderIdLayout.MinimumPayloadBytes = 8 && orderIdLayout.MinimumExtentBytes = 8
            && not orderIdLayout.IsDynamic
            && metersLayout.Type <> orderIdLayout.Type)
        check ($"{optimization} nominal Int descriptors keep Int kind and distinct TypeIds") (
            identity.LlvmIr.Contains("i32 1, i32 4, i32 0, i32 0, i32 8, i32 8, i32 8, i32 8, i32 0", StringComparison.Ordinal)
            && identity.LlvmIr.Contains("i32 1, i32 5, i32 0, i32 0, i32 8, i32 8, i32 8, i32 8, i32 0", StringComparison.Ordinal))
        for label, number, rawBytes in fixtures do
            let result =
                identity.Execute(
                    [ NamedValue("Meters", IntValue number); NamedValue("OrderId", IntValue number) ],
                    4096,
                    16)
            check ($"{optimization} {label} exact nominal host roundtrip") (
                result.Values = [ NamedValue("Meters", IntValue number); NamedValue("OrderId", IntValue number) ]
                && result.LayoutSchemaVersion = 3
                && result.RetainedBytesWritten = 16
                && Convert.ToHexString(result.RetainedOutputBytes).ToLowerInvariant() = rawBytes + rawBytes)

        use wrapUnwrap = compileOwningNative toolchain "owning-nominal-wrap-unwrap" optimization wrapUnwrapBody
        for label, number, rawBytes in fixtures do
            let result = wrapUnwrap.Execute([ IntValue number ], 4096, 8)
            check ($"{optimization} {label} wrap/unwrap preserves the raw Int payload") (
                result.Values = [ IntValue number ]
                && Convert.ToHexString(result.RetainedOutputBytes).ToLowerInvariant() = rawBytes
                && result.Metrics.DeepCopyBytes = 0UL
                && result.Metrics.MoveBytes = 0UL)

        let sentinel = Array.create 16 0xA5uy
        let rejectsWithoutPublishing name invalidValue =
            let mutable rejected = false
            try
                identity.ExecuteInto(
                    [ invalidValue; NamedValue("OrderId", IntValue 0L) ],
                    4096,
                    sentinel)
                |> ignore
            with :? ArgumentException -> rejected <- true
            check ($"{optimization} {name} is rejected by nominal host validation") rejected
            check ($"{optimization} {name} rejection leaves retained output unchanged") (sentinel = Array.create 16 0xA5uy)
        rejectsWithoutPublishing "bare Int" (IntValue 0L)
        rejectsWithoutPublishing "wrong nominal" (NamedValue("OrderId", IntValue 0L))
        rejectsWithoutPublishing "same-name RecordValue" (RecordValue("Meters", Map.empty))

    let record = recordDefinition "ZEnvelope" [ recordField "owner" (TNamed "Meters") ]
    let nestedContext =
        contextWithRecordDefinitions [] [ record ] [
            scalarDefinition "Meters" TInt None, "Meters.create", "Meters.raw"
            scalarDefinition "OrderId" TInt None, "OrderId.create", "OrderId.raw"
        ]
    let nestedBody = compileBody nestedContext "owning-nominal-nested" [
        Push(LInt -42L, span "owning-nested.agent" 1)
        Call("Meters.create", span "owning-nested.agent" 2)
        Call("zEnvelope.new", span "owning-nested.agent" 3)
        Push(LInt -42L, span "owning-nested.agent" 4)
        Call("Meters.create", span "owning-nested.agent" 5)
        ConstructContainer(OptionSome, [ TNamed "Meters" ], span "owning-nested.agent" 6)
        ConstructContainer(OptionNone, [ TNamed "Meters" ], span "owning-nested.agent" 7)
        Push(LInt -42L, span "owning-nested.agent" 8)
        Call("Meters.create", span "owning-nested.agent" 9)
        ConstructContainer(ResultOk, [ TNamed "Meters"; TNamed "OrderId" ], span "owning-nested.agent" 10)
        Push(LInt -42L, span "owning-nested.agent" 11)
        Call("OrderId.create", span "owning-nested.agent" 12)
        ConstructContainer(ResultError, [ TNamed "Meters"; TNamed "OrderId" ], span "owning-nested.agent" 13)
    ]
    let nestedExpected = [
        RecordValue("ZEnvelope", Map.ofList [ "owner", NamedValue("Meters", IntValue -42L) ])
        OptionValue(TNamed "Meters", Some(NamedValue("Meters", IntValue -42L)))
        OptionValue(TNamed "Meters", None)
        ResultValue(TNamed "Meters", TNamed "OrderId", Ok(NamedValue("Meters", IntValue -42L)))
        ResultValue(TNamed "Meters", TNamed "OrderId", Error(NamedValue("OrderId", IntValue -42L)))
    ]
    let nestedBytes = "d6ffffffffffffff0000000000000000d6ffffffffffffff01000000000000000000000000000000d6ffffffffffffff0100000000000000d6ffffffffffffff"
    for optimization in [ LlvmOptimization.O0; LlvmOptimization.O2 ] do
        use nested = compileOwningNative toolchain "owning-nominal-nested" optimization nestedBody
        let result = nested.Execute([], 4096, 64)
        check ($"{optimization} scalar identity survives nested record, Option, and Result values") (
            result.Values = nestedExpected
            && result.RetainedBytesWritten = 64
            && Convert.ToHexString(result.RetainedOutputBytes).ToLowerInvariant() = nestedBytes)
        let metersLayout = nested.Layouts |> List.find (fun layout -> layout.TypeName = "Meters")
        let orderIdLayout = nested.Layouts |> List.find (fun layout -> layout.TypeName = "OrderId")
        let envelopeLayout = nested.Layouts |> List.find (fun layout -> layout.TypeName = "ZEnvelope")
        check ($"{optimization} nested scalar descriptors retain separate identity") (
            metersLayout.Type = IrNominal(ProgramTypeKey 0)
            && orderIdLayout.Type = IrNominal(ProgramTypeKey 1)
            && envelopeLayout.Type = IrNominal(ProgramTypeKey 2)
            && metersLayout.Type <> orderIdLayout.Type
            && (envelopeLayout.Fields |> List.exists (fun field -> field.FieldName = "owner" && field.FieldType = metersLayout.Type)))

    let ownerProvenanceContext =
        contextWithRecordDefinitions [] [
            recordDefinition "OwnerEnvelope" [
                recordField "owner" (TNamed "Meters")
                recordField "tail" TString
            ]
        ] [
            scalarDefinition "Meters" TInt None, "Meters.create", "Meters.raw"
            scalarDefinition "OrderId" TInt None, "OrderId.create", "OrderId.raw"
        ]
    let ownerProvenanceBody = compileBodyWithInputs ownerProvenanceContext "owning-nominal-owner-provenance" [ TNamed "OwnerEnvelope" ] [
        Call("ownerEnvelope.owner", span "owning-provenance.agent" 1)
        Call("Meters.raw", span "owning-provenance.agent" 2)
    ]
    for optimization in [ LlvmOptimization.O0; LlvmOptimization.O2 ] do
        use ownerProvenance = compileOwningNative toolchain "owning-nominal-owner-provenance" optimization ownerProvenanceBody
        let result =
            ownerProvenance.Execute(
                [ RecordValue("OwnerEnvelope", Map.ofList [ "owner", NamedValue("Meters", IntValue -42L); "tail", StringValue "tail" ]) ],
                4096,
                8)
        let preservesFullOwnerEnd =
            result.LayoutEvents
            |> List.exists (fun event ->
                event.Kind = "descriptor-transfer"
                && event.TypeId = 1u
                && event.OffsetBytes = 0
                && event.SourceOffsetBytes = Some 24
                && event.SourceExtentBytes = Some 8
                && 24 > event.OffsetBytes + 8)
        check ($"{optimization} scalar unwrap descriptor retains the multi-field owner's end") (
            result.Values = [ IntValue -42L ]
            && Convert.ToHexString(result.RetainedOutputBytes).ToLowerInvariant() = "d6ffffffffffffff"
            && result.Metrics.InputBytes = 24
            && result.Metrics.HostEncodedInputBytes = 24
            && result.Metrics.DeepCopyBytes = 0UL
            && result.Metrics.MoveBytes = 0UL
            && not result.Metrics.TraceTruncated
            && preservesFullOwnerEnd)

    let rejectScalar name baseType =
        let context = contextWithScalars [] [ scalarDefinition name baseType None ]
        let body = compileBodyWithInputs context ("unsupported-" + name) [ TNamed name ] []
        let diagnostic = errorOf (fun () -> compileOwningNative toolchain ("unsupported-" + name) LlvmOptimization.O0 body |> ignore)
        check ($"{name} remains outside the owning nominal Int/String slice") (
            diagnostic.Code = "IR_OWNING_STACK_TYPE_UNSUPPORTED"
            && diagnostic.Message.Contains("Float and other scalar bases remain unsupported", StringComparison.OrdinalIgnoreCase))
    rejectScalar "FloatTag" TFloat
    let inactiveContext = contextWithScalars [] [ scalarDefinition "InactiveFloat" TFloat None ]
    let inactiveBody = compileBodyWithInputs inactiveContext "unsupported-inactive-result-alternative" [ TResult(TInt, TNamed "InactiveFloat") ] []
    let inactiveError = errorOf (fun () -> compileOwningNative toolchain "unsupported-inactive-result-alternative" LlvmOptimization.O0 inactiveBody |> ignore)
    check "unsupported scalar is rejected in an inactive Result alternative" (
        inactiveError.Code = "IR_OWNING_STACK_TYPE_UNSUPPORTED"
        && inactiveError.Message.Contains("Float and other scalar bases remain unsupported", StringComparison.OrdinalIgnoreCase))

let private testOwningNominalBoolSlice () =
    let toolchain = LlvmToolchain.discover()
    let scalarContext =
        contextWithScalarDefinitions [] [
            scalarDefinition "BoolTag" TBool None, "BoolTag.make", "BoolTag.value"
            scalarDefinition "PeerBoolTag" TBool None, "PeerBoolTag.make", "PeerBoolTag.value"
        ]
    let boolTagType = IrNominal(ProgramTypeKey 0)
    let peerBoolTagType = IrNominal(ProgramTypeKey 1)
    let identityBody = compileBodyWithInputs scalarContext "owning-nominal-bool-identity" [ TNamed "BoolTag"; TNamed "PeerBoolTag" ] []
    let parityBody = compileBodyWithInputs scalarContext "owning-nominal-bool-interpreter-parity" [] [
        Push(LBool true, span "owning-nominal-bool-parity.agent" 1)
        Call("BoolTag.make", span "owning-nominal-bool-parity.agent" 2)
        Call("BoolTag.value", span "owning-nominal-bool-parity.agent" 3)
        Push(LBool false, span "owning-nominal-bool-parity.agent" 4)
        Call("PeerBoolTag.make", span "owning-nominal-bool-parity.agent" 5)
        Call("PeerBoolTag.value", span "owning-nominal-bool-parity.agent" 6)
    ]
    let wrapUnwrapBody = compileBodyWithInputs scalarContext "owning-nominal-bool-wrap-unwrap" [ TBool ] [
        Call("BoolTag.make", span "owning-nominal-bool.agent" 1)
        Call("BoolTag.value", span "owning-nominal-bool.agent" 2)
    ]
    let raw flag = if flag then "0100000000000000" else "0000000000000000"
    let sentinel = Array.create 16 0xA5uy

    for optimization in [ LlvmOptimization.O0; LlvmOptimization.O2 ] do
        use identity = compileOwningNative toolchain "owning-nominal-bool-identity" optimization identityBody
        let boolLayout = identity.Layouts |> List.find (fun layout -> layout.TypeName = "BoolTag")
        let peerLayout = identity.Layouts |> List.find (fun layout -> layout.TypeName = "PeerBoolTag")
        check ($"{optimization} BoolTag keeps a fixed eight-byte nominal layout") (
            boolLayout.Type = boolTagType && boolLayout.PayloadBytes = 8 && boolLayout.ExtentBytes = 8
            && boolLayout.MinimumPayloadBytes = 8 && boolLayout.MinimumExtentBytes = 8 && not boolLayout.IsDynamic)
        check ($"{optimization} peer Bool wrappers keep separate exact identities") (
            peerLayout.Type = peerBoolTagType && peerLayout.Type <> boolLayout.Type
            && peerLayout.PayloadBytes = 8 && peerLayout.ExtentBytes = 8)
        check ($"{optimization} Bool wrappers use kind 2 and distinct nominal TypeIds") (
            identity.LlvmIr.Contains("i32 2, i32 4, i32 0, i32 0, i32 8, i32 8, i32 8, i32 8, i32 0", StringComparison.Ordinal)
            && identity.LlvmIr.Contains("i32 2, i32 5, i32 0, i32 0, i32 8, i32 8, i32 8, i32 8, i32 0", StringComparison.Ordinal))

        for flag in [ false; true ] do
            let values = [ NamedValue("BoolTag", BoolValue flag); NamedValue("PeerBoolTag", BoolValue flag) ]
            let actual = identity.Execute(values, 4096, 16)
            check ($"{optimization} {flag} exact-name nominal Bool host roundtrip") (
                actual.Values = values && actual.LayoutSchemaVersion = 3
                && actual.RetainedBytesWritten = 16
                && Convert.ToHexString(actual.RetainedOutputBytes).ToLowerInvariant() = raw flag + raw flag)

        use wrapUnwrap = compileOwningNative toolchain "owning-nominal-bool-wrap-unwrap" optimization wrapUnwrapBody
        for flag in [ false; true ] do
            let actual = wrapUnwrap.Execute([ BoolValue flag ], 4096, 8)
            check ($"{optimization} {flag} Bool wrap and unwrap retag without payload movement") (
                actual.Values = [ BoolValue flag ]
                && Convert.ToHexString(actual.RetainedOutputBytes).ToLowerInvariant() = raw flag
                && actual.Metrics.DeepCopyBytes = 0UL && actual.Metrics.MoveBytes = 0UL)

        use parity = compileOwningNative toolchain "owning-nominal-bool-interpreter-parity" optimization parityBody
        let interpreted = interpreterResult "owning-nominal-bool-interpreter-parity" parityBody
        let nativeParity = parity.Execute([], 4096, 16)
        check ($"{optimization} nominal Bool wrap/unwrap native execution agrees with interpreter results") (
            nativeParity.Values = interpreted && nativeParity.Values = [ BoolValue true; BoolValue false ]
            && Convert.ToHexString(nativeParity.RetainedOutputBytes).ToLowerInvariant() = raw true + raw false)

        for label, wrongValue in [ "bare Bool", BoolValue false; "peer wrapper", NamedValue("PeerBoolTag", BoolValue false) ] do
            let rejected =
                try
                    identity.ExecuteInto([ wrongValue; NamedValue("PeerBoolTag", BoolValue true) ], 4096, sentinel) |> ignore
                    false
                with :? ArgumentException -> true
            check ($"{optimization} host rejects {label} for BoolTag") (rejected && sentinel = Array.create 16 0xA5uy)

    let boolEnvelope = recordDefinition "BoolEnvelope" [
        recordField "owner" (TNamed "BoolTag")
        recordField "plain" TBool
    ]
    let nestedContext =
        contextWithRecordDefinitions [] [ boolEnvelope ] [
            scalarDefinition "BoolTag" TBool None, "BoolTag.make", "BoolTag.value"
            scalarDefinition "PeerBoolTag" TBool None, "PeerBoolTag.make", "PeerBoolTag.value"
        ]
    let nestedTypes = [
        TNamed "BoolEnvelope"
        TOption(TNamed "BoolTag")
        TOption(TNamed "BoolTag")
        TResult(TNamed "BoolTag", TNamed "PeerBoolTag")
        TResult(TNamed "BoolTag", TNamed "PeerBoolTag")
        TResult(TNamed "PeerBoolTag", TNamed "BoolTag")
    ]
    let nestedBody = compileBodyWithInputs nestedContext "owning-nominal-bool-nested" nestedTypes []
    let boolTag flag = NamedValue("BoolTag", BoolValue flag)
    let peerBoolTag flag = NamedValue("PeerBoolTag", BoolValue flag)
    let nestedValues = [
        RecordValue("BoolEnvelope", Map.ofList [ "owner", boolTag true; "plain", BoolValue false ])
        OptionValue(TNamed "BoolTag", Some(boolTag false))
        OptionValue(TNamed "BoolTag", None)
        ResultValue(TNamed "BoolTag", TNamed "PeerBoolTag", Ok(boolTag true))
        ResultValue(TNamed "BoolTag", TNamed "PeerBoolTag", Error(peerBoolTag true))
        ResultValue(TNamed "PeerBoolTag", TNamed "BoolTag", Error(boolTag false))
    ]
    let zero = raw false
    let one = raw true
    let expectedBytes = String.concat "" [ one; zero; zero; zero; one; zero; one; one; one; one; zero ]
    for optimization in [ LlvmOptimization.O0; LlvmOptimization.O2 ] do
        use nested = compileOwningNative toolchain "owning-nominal-bool-nested" optimization nestedBody
        let actual = nested.Execute(nestedValues, 4096, 88)
        check ($"{optimization} nominal Bool survives record, Option, and both active Result arms") (
            actual.Values = nestedValues && actual.RetainedBytesWritten = 88
            && Convert.ToHexString(actual.RetainedOutputBytes).ToLowerInvariant() = expectedBytes)

let private testOwningRefinedBoolSlice () =
    let toolchain = LlvmToolchain.discover()
    let owningException action =
        try
            action ()
            failwith "Expected an OwningStackExecutionException."
        with :? OwningStackExecutionException as error -> error
    let owningError action = (owningException action).Diagnostic
    let trueValidator = wordEntry "is-true?" [ TBool ] [ TBool ] Set.empty [
        Call("bool.not", span "owning-bool-validator.agent" 1)
        Call("bool.not", span "owning-bool-validator.agent" 2)
    ]
    let falseValidator = wordEntry "is-false?" [ TBool ] [ TBool ] Set.empty [ Call("bool.not", span "owning-bool-validator.agent" 1) ]
    let scalarDefinitions = [
        scalarDefinition "TrueTag" TBool (Some "is-true?"), "TrueTag.make", "TrueTag.unwrap"
        scalarDefinition "FalseTag" TBool (Some "is-false?"), "FalseTag.make", "FalseTag.unwrap"
    ]
    let context = contextWithScalarDefinitions [ trueValidator; falseValidator ] scalarDefinitions
    // ProgramTypeKey allocation is name-sorted, so FalseTag precedes TrueTag.
    let trueTagType = IrNominal(ProgramTypeKey 1)
    let falseTagType = IrNominal(ProgramTypeKey 0)
    let trueValue = NamedValue("TrueTag", BoolValue true)
    let falseValue = NamedValue("FalseTag", BoolValue false)
    let raw flag = if flag then Convert.FromHexString "0100000000000000" else Array.zeroCreate<byte> 8
    let constructorBodies = [
        "TrueTag", "TrueTag.make", compileBodyWithInputs context "owning-refined-bool-true-constructor" [ TBool ] [ Call("TrueTag.make", span "owning-refined-bool.agent" 1) ]
        "FalseTag", "FalseTag.make", compileBodyWithInputs context "owning-refined-bool-false-constructor" [ TBool ] [ Call("FalseTag.make", span "owning-refined-bool.agent" 2) ]
    ]
    let identityBody = compileBodyWithInputs context "owning-refined-bool-input-only" [ TNamed "TrueTag"; TNamed "FalseTag" ] []
    let trueTagValidatorStepModel = compileBodyWithInputs context "owning-refined-bool-true-admission-step-model" [ TBool ] [
        Call("bool.not", span "owning-refined-bool-validator.agent" 1)
        Call("bool.not", span "owning-refined-bool-validator.agent" 2)
    ]
    let falseTagValidatorStepModel = compileBodyWithInputs context "owning-refined-bool-false-admission-step-model" [ TBool ] [
        Call("bool.not", span "owning-refined-bool-validator.agent" 3)
    ]
    let _, trueTagAdmissionSteps =
        interpreterResultWithInputsAndSteps "owning-refined-bool-true-admission-step-model" trueTagValidatorStepModel [ IrEntryArgument.BoolArgument true ]
    let _, falseTagAdmissionSteps =
        interpreterResultWithInputsAndSteps "owning-refined-bool-false-admission-step-model" falseTagValidatorStepModel [ IrEntryArgument.BoolArgument false ]
    let expectedIdentityAdmissionSteps = uint32 (trueTagAdmissionSteps + falseTagAdmissionSteps)
    check "refined Bool input validators have an independent combined step oracle" (trueTagAdmissionSteps = 2 && falseTagAdmissionSteps = 1)
    let validatorParityBody = compileBodyWithInputs context "owning-refined-bool-interpreter-parity" [] [
        Push(LBool true, span "owning-refined-bool-parity.agent" 1)
        Call("TrueTag.make", span "owning-refined-bool-parity.agent" 2)
        Push(LBool false, span "owning-refined-bool-parity.agent" 3)
        Call("FalseTag.make", span "owning-refined-bool-parity.agent" 4)
    ]
    let roundtripBody = compileBodyWithInputs context "owning-refined-bool-wrap-unwrap" [ TBool ] [
        Call("TrueTag.make", span "owning-refined-bool.agent" 3)
        Call("TrueTag.unwrap", span "owning-refined-bool.agent" 4)
    ]

    for optimization in [ LlvmOptimization.O0; LlvmOptimization.O2 ] do
        for scalarName, constructorName, body in constructorBodies do
            use constructor = compileOwningNative toolchain ("owning-refined-bool-" + scalarName) optimization body
            let nominalLayout = constructor.Layouts |> List.find (fun layout -> layout.TypeName = scalarName)
            check ($"{optimization} {scalarName} keeps its nominal Bool identity and fixed eight-byte layout") (
                nominalLayout.Type = (if scalarName = "TrueTag" then trueTagType else falseTagType)
                && nominalLayout.PayloadBytes = 8 && nominalLayout.ExtentBytes = 8
                && nominalLayout.MinimumPayloadBytes = 8 && nominalLayout.MinimumExtentBytes = 8)
            let expectedNominalTypeId = if scalarName = "TrueTag" then 5 else 4
            check ($"{optimization} {scalarName} descriptor uses Bool kind 2 and a distinct nominal TypeId") (
                constructor.LlvmIr.Contains("i32 2, i32 2, i32 0, i32 0, i32 8, i32 8, i32 8, i32 8, i32 0", StringComparison.Ordinal)
                && constructor.LlvmIr.Contains(
                    $"i32 2, i32 {expectedNominalTypeId}, i32 0, i32 0, i32 8, i32 8, i32 8, i32 8, i32 0",
                    StringComparison.Ordinal))
            let acceptedFlag = scalarName = "TrueTag"
            let constructed = constructor.Execute([ BoolValue acceptedFlag ], 4096, 8)
            let interpretedConstructor, interpreterSteps =
                interpreterResultWithInputsAndSteps constructorName body [ IrEntryArgument.BoolArgument acceptedFlag ]
            let rawStatus, rawContext, _ = invokeRawOwningEntryForMetrics constructor (raw acceptedFlag) [| 8 |]
            check ($"{optimization} {constructorName} independently accepts its matching Bool") (
                constructed.Values = [ NamedValue(scalarName, BoolValue acceptedFlag) ]
                && constructed.Values = interpretedConstructor
                && rawStatus = 0 && rawContext.Status = 0u
                && rawContext.StepsConsumed = uint32 interpreterSteps
                && constructed.RetainedOutputBytes = raw acceptedFlag
                && constructed.Metrics.DeepCopyBytes = 0UL && constructed.Metrics.MoveBytes = 0UL)
            let rejectedFlag = not acceptedFlag
            let rejection = owningError (fun () -> constructor.Execute([ BoolValue rejectedFlag ], 4096, 8) |> ignore)
            check ($"{optimization} {constructorName} independently rejects the opposite Bool") (
                rejection.Code = "REFINEMENT_FAILED" && rejection.Word = Some constructorName)

        use identity = compileOwningNative toolchain "owning-refined-bool-input-only" optimization identityBody
        let trueLayout = identity.Layouts |> List.find (fun layout -> layout.TypeName = "TrueTag")
        let falseLayout = identity.Layouts |> List.find (fun layout -> layout.TypeName = "FalseTag")
        check ($"{optimization} refined Bool wrappers remain distinct exact nominal types") (
            trueLayout.Type = trueTagType && falseLayout.Type = falseTagType
            && trueLayout.Type <> falseLayout.Type && trueLayout.PayloadBytes = 8 && falseLayout.PayloadBytes = 8)
        let identityResult = identity.Execute([ trueValue; falseValue ], 4096, 16)
        check ($"{optimization} valid refined Bool wrappers host-encode and roundtrip exact bytes") (
            identityResult.Values = [ trueValue; falseValue ]
            && identityResult.RetainedOutputBytes = Array.append (raw true) (raw false))
        let identityRawStatus, identityRawContext, _ =
            invokeRawOwningEntryForMetrics identity (Array.append (raw true) (raw false)) [| 8; 8 |]
        check ($"{optimization} typed-root admission runs TrueTag and FalseTag validators exactly once (steps={identityRawContext.StepsConsumed}/{expectedIdentityAdmissionSteps})") (
            identityRawStatus = 0 && identityRawContext.Status = 0u
            && identityRawContext.StepsConsumed = expectedIdentityAdmissionSteps)
        use validatorParity = compileOwningNative toolchain "owning-refined-bool-interpreter-parity" optimization validatorParityBody
        let interpretedValidators = interpreterResult "owning-refined-bool-interpreter-parity" validatorParityBody
        let nativeValidators = validatorParity.Execute([], 4096, 16)
        check ($"{optimization} frozen refined Bool validator calls agree with interpreter results") (
            nativeValidators.Values = interpretedValidators
            && nativeValidators.Values = [ trueValue; falseValue ]
            && nativeValidators.RetainedOutputBytes = Array.append (raw true) (raw false))
        for label, badValue in [ "bare Bool", BoolValue true; "wrong peer wrapper", NamedValue("FalseTag", BoolValue true) ] do
            let retained = Array.create 16 0xA5uy
            let mutable rejected = false
            try
                identity.ExecuteInto([ badValue; falseValue ], 4096, retained) |> ignore
            with :? ArgumentException -> rejected <- true
            check ($"{optimization} refined Bool host rejects {label} for TrueTag") (rejected && retained = Array.create 16 0xA5uy)
        let semanticRetained = Array.create 16 0xA5uy
        let semanticFailure = owningException (fun () -> identity.ExecuteInto([ NamedValue("TrueTag", BoolValue false); falseValue ], 4096, semanticRetained) |> ignore)
        check ($"{optimization} raw canonical False fails TrueTag input revalidation atomically") (
            semanticFailure.Diagnostic.Code = "REFINEMENT_FAILED"
            && semanticRetained = Array.create 16 0xA5uy
            && semanticFailure.Metrics.FinalCursorBytes = 0
            && semanticFailure.Metrics.HostRetainedCommitBytes = 0)

        use roundtrip = compileOwningNative toolchain "owning-refined-bool-wrap-unwrap" optimization roundtripBody
        let wrapped = roundtrip.Execute([ BoolValue true ], 4096, 8)
        let interpretedRoundtrip, roundtripInterpreterSteps =
            interpreterResultWithInputsAndSteps "owning-refined-bool-wrap-unwrap" roundtripBody [ IrEntryArgument.BoolArgument true ]
        let roundtripRawStatus, roundtripRawContext, _ = invokeRawOwningEntryForMetrics roundtrip (raw true) [| 8 |]
        check ($"{optimization} refined Bool wrap/unwrap retags without moving payload") (
            wrapped.Values = [ BoolValue true ] && wrapped.Values = interpretedRoundtrip
            && roundtripRawStatus = 0 && roundtripRawContext.Status = 0u
            && roundtripRawContext.StepsConsumed = uint32 roundtripInterpreterSteps
            && wrapped.RetainedOutputBytes = raw true
            && wrapped.Metrics.DeepCopyBytes = 0UL && wrapped.Metrics.MoveBytes = 0UL)

        let nestedTypes = [
            TOption(TNamed "TrueTag")
            TOption(TNamed "TrueTag")
            TResult(TNamed "TrueTag", TString)
            TResult(TString, TNamed "TrueTag")
        ]
        let nestedBody = compileBodyWithInputs context "owning-refined-bool-nested" nestedTypes []
        use nested = compileOwningNative toolchain "owning-refined-bool-nested" optimization nestedBody
        let inactiveNestedValues = [
            OptionValue(TNamed "TrueTag", None)
            OptionValue(TNamed "TrueTag", Some trueValue)
            ResultValue(TNamed "TrueTag", TString, Error(StringValue "inactive"))
            ResultValue(TString, TNamed "TrueTag", Ok(StringValue "inactive"))
        ]
        check ($"{optimization} inactive Option and Result Bool arms skip the frozen validator") (
            nested.Execute(inactiveNestedValues, 4096, 88).Values = inactiveNestedValues)
        for label, invalidValue in [
            "Option Some", OptionValue(TNamed "TrueTag", Some(NamedValue("TrueTag", BoolValue false)))
            "Result Ok", ResultValue(TNamed "TrueTag", TString, Ok(NamedValue("TrueTag", BoolValue false)))
            "Result Error", ResultValue(TString, TNamed "TrueTag", Error(NamedValue("TrueTag", BoolValue false)))
        ] do
            let invalidIndex = if label = "Option Some" then 1 elif label = "Result Ok" then 2 else 3
            let values = inactiveNestedValues |> List.mapi (fun index value -> if index = invalidIndex then invalidValue else value)
            let error = owningError (fun () -> nested.Execute(values, 4096, 88) |> ignore)
            check ($"{optimization} active {label} runs the frozen Bool validator") (error.Code = "REFINEMENT_FAILED")

    let mailboxContext =
        contextWithRecordDefinitions [ trueValidator ] [
            recordDefinition "MailboxState" [ recordField "owner" (TNamed "TrueTag") ]
            recordDefinition "MailboxContinuation" [ recordField "marker" TInt ]
        ] [ scalarDefinitions[0] ]
    let mailboxProgram = Compiler.compileIrProgram mailboxContext
    let initialize = Compiler.compileIrBodyAgainstProgram mailboxContext mailboxProgram "owning-refined-bool-mailbox-initialize" [ TString ] [
        Call("drop", span "owning-refined-bool-mailbox.agent" 1)
        Push(LBool true, span "owning-refined-bool-mailbox.agent" 2)
        Call("TrueTag.make", span "owning-refined-bool-mailbox.agent" 3)
        Call("mailboxState.new", span "owning-refined-bool-mailbox.agent" 4)
    ]
    let beginTurn = Compiler.compileIrBodyAgainstProgram mailboxContext mailboxProgram "owning-refined-bool-mailbox-begin" [ TNamed "MailboxState"; TString ] [
        Call("drop", span "owning-refined-bool-mailbox.agent" 5)
        Push(LInt 1L, span "owning-refined-bool-mailbox.agent" 6)
        Call("mailboxContinuation.new", span "owning-refined-bool-mailbox.agent" 7)
    ]
    let beginStepModel = Compiler.compileIrBodyAgainstProgram mailboxContext mailboxProgram "owning-refined-bool-mailbox-begin-step-model" [ TNamed "MailboxState"; TBool ] [
        Call("drop", span "owning-refined-bool-mailbox.agent" 5)
        Push(LInt 1L, span "owning-refined-bool-mailbox.agent" 6)
        Call("mailboxContinuation.new", span "owning-refined-bool-mailbox.agent" 7)
    ]
    let resume = Compiler.compileIrBodyAgainstProgram mailboxContext mailboxProgram "owning-refined-bool-mailbox-resume" [ TNamed "MailboxState"; TNamed "MailboxContinuation"; TString ] [
        Call("drop", span "owning-refined-bool-mailbox.agent" 8)
        Call("drop", span "owning-refined-bool-mailbox.agent" 9)
    ]
    let resumeStepModel = Compiler.compileIrBodyAgainstProgram mailboxContext mailboxProgram "owning-refined-bool-mailbox-resume-step-model" [ TNamed "MailboxState"; TNamed "MailboxContinuation"; TUnit ] [
        Call("drop", span "owning-refined-bool-mailbox.agent" 8)
        Call("drop", span "owning-refined-bool-mailbox.agent" 9)
    ]
    let predicateStepBody = Compiler.compileIrBodyAgainstProgram mailboxContext mailboxProgram "owning-refined-bool-validator-step-model" [ TBool ] [
        Call("bool.not", span "owning-refined-bool-validator.agent" 1)
        Call("bool.not", span "owning-refined-bool-validator.agent" 2)
    ]
    let _, frozenValidatorSteps =
        interpreterResultWithInputsAndSteps "owning-refined-bool-validator-step-model" predicateStepBody [ IrEntryArgument.BoolArgument true ]
    let expectedValidatorSteps = uint32 frozenValidatorSteps
    check "two-instruction frozen TrueTag validator step oracle" (frozenValidatorSteps = 2)

    let trueTagOwnerBody = Compiler.compileIrBodyAgainstProgram mailboxContext mailboxProgram "owning-refined-bool-mailbox-state-owner" [] [
        Push(LBool true, span "owning-refined-bool-mailbox-owner.agent" 1)
        Call("TrueTag.make", span "owning-refined-bool-mailbox-owner.agent" 2)
        Call("mailboxState.new", span "owning-refined-bool-mailbox-owner.agent" 3)
    ]
    let resumeOwnerBody = Compiler.compileIrBodyAgainstProgram mailboxContext mailboxProgram "owning-refined-bool-mailbox-resume-owner" [] [
        Push(LBool true, span "owning-refined-bool-mailbox-owner.agent" 4)
        Call("TrueTag.make", span "owning-refined-bool-mailbox-owner.agent" 5)
        Call("mailboxState.new", span "owning-refined-bool-mailbox-owner.agent" 6)
        Push(LInt 1L, span "owning-refined-bool-mailbox-owner.agent" 7)
        Call("mailboxContinuation.new", span "owning-refined-bool-mailbox-owner.agent" 8)
    ]
    let noReplayContext =
        contextWithRecordDefinitions [] [
            recordDefinition "MailboxState" [ recordField "owner" (TNamed "TrueTag") ]
            recordDefinition "MailboxContinuation" [ recordField "marker" TInt ]
        ] [ scalarDefinition "TrueTag" TBool None, "TrueTag.make", "TrueTag.unwrap" ]
    let noReplayProgram = Compiler.compileIrProgram noReplayContext
    let noReplayResume = Compiler.compileIrBodyAgainstProgram noReplayContext noReplayProgram "owning-refined-bool-mailbox-resume-no-validator" [ TNamed "MailboxState"; TNamed "MailboxContinuation"; TUnit ] [
        Call("drop", span "owning-refined-bool-mailbox.agent" 8)
        Call("drop", span "owning-refined-bool-mailbox.agent" 9)
    ]
    let noReplayOwnerBody = Compiler.compileIrBodyAgainstProgram noReplayContext noReplayProgram "owning-refined-bool-mailbox-resume-owner-no-validator" [] [
        Push(LBool false, span "owning-refined-bool-mailbox-owner.agent" 9)
        Call("TrueTag.make", span "owning-refined-bool-mailbox-owner.agent" 10)
        Call("mailboxState.new", span "owning-refined-bool-mailbox-owner.agent" 11)
        Push(LInt 1L, span "owning-refined-bool-mailbox-owner.agent" 12)
        Call("mailboxContinuation.new", span "owning-refined-bool-mailbox-owner.agent" 13)
    ]
    use noReplayOwner = IrInterpreter.executeBodyWithInputs (noOpHost ()) "owning-refined-bool-mailbox-resume-owner-no-validator" noReplayOwnerBody None []
    let _, noReplayResumeSteps =
        interpreterResultWithOwnerAndSteps
            "owning-refined-bool-mailbox-resume-no-validator"
            noReplayResume
            (Some noReplayOwner)
            [ IrEntryArgument.RetainedRoot 0; IrEntryArgument.RetainedRoot 1; IrEntryArgument.UnitArgument ]
    let expectedNoReplaySteps = uint32 noReplayResumeSteps

    let malformedRawBoolBody = compileBodyWithInputs context "owning-refined-bool-raw-malformed-entry" [ TBool ] [
        Call("drop", span "owning-refined-bool-malformed.agent" 1)
    ]

    for optimization in [ LlvmOptimization.O0; LlvmOptimization.O2 ] do
        use malformedRawBool = compileOwningNative toolchain "owning-refined-bool-raw-malformed-entry" optimization malformedRawBoolBody
        let rawBadBoolStatus, rawBadBoolContext, rawBadBoolRetained =
            invokeRawOwningEntryForMetrics malformedRawBool (Convert.FromHexString "0200000000000000") [| 8 |]
        check ($"{optimization} raw primitive Bool import rejects noncanonical eight-byte value before body") (
            rawBadBoolStatus <> 0 && rawBadBoolContext.Status <> 0u
            && rawBadBoolContext.CursorBytes = 0u && rawBadBoolContext.StepsConsumed = 0u
            && rawBadBoolRetained = Array.create rawBadBoolRetained.Length 0xA5uy)

        let mailbox =
            OwningStackAot.compileMailboxWithProfile
                toolchain optimization OwningRuntimeProfile.Diagnostic
                (Path.Combine(artifactRoot, "owning-refined-bool-mailbox", string optimization))
                initialize beginTurn resume
        check ($"{optimization} mailbox layout admits refined Bool roots without ABI revision") (
            mailbox.Entries.Length = 3
            && (mailbox.Layouts |> List.exists (fun layout -> layout.TypeName = "TrueTag"))
            && mailbox.AssociatedResumeSymbol = "agentlang_mailbox_resume_associated")
        let completion = mailboxStringBytes "turn"
        let expectedResumeInputCursor =
            mailbox.Entries[2].InputTypes
            |> List.sumBy (fun inputType ->
                match inputType with
                | IrString -> completion.Length
                | _ ->
                    mailbox.Layouts
                    |> List.find (fun layout -> layout.Type = inputType)
                    |> fun layout -> layout.ExtentBytes)
        // RETURN imports every entry root before semantic validation. The
        // frozen Bool->Bool validator frame holds one input and one result.
        let validatorBoolExtent =
            mailbox.Layouts
            |> List.find (fun layout -> layout.Type = IrBool)
            |> fun layout -> layout.ExtentBytes
        let expectedSemanticReturnCursor = expectedResumeInputCursor + 2 * validatorBoolExtent
        let goodState = raw true
        let badCanonicalState = raw false
        let malformedState = Convert.FromHexString "0200000000000000"
        use beginOwner = IrInterpreter.executeBodyWithInputs (noOpHost ()) "owning-refined-bool-mailbox-state-owner" trueTagOwnerBody None []
        let _, expectedBeginSteps =
            interpreterResultWithOwnerAndSteps
                "owning-refined-bool-mailbox-begin"
                beginStepModel
                (Some beginOwner)
                [ IrEntryArgument.RetainedRoot 0; IrEntryArgument.BoolArgument true ]
        let successfulBegin = invokeRawMailboxEntry mailbox 1 [ goodState; completion ]
        check ($"{optimization} mailbox begin imports a valid refined Bool state (status={successfulBegin.Status}, callDepth={successfulBegin.Context.CallDepth}, steps={successfulBegin.Context.StepsConsumed}/{expectedBeginSteps + int expectedValidatorSteps}, outputs={successfulBegin.Outputs |> List.map (fun (output: RawMailboxOutputSlice) -> output.TypeIndex, output.OffsetBytes, output.OwnerEndBytes, output.Reserved)})") (
            successfulBegin.Status = 0 && successfulBegin.Context.CallDepth = 0u
            && successfulBegin.Context.StepsConsumed = uint32 expectedBeginSteps + expectedValidatorSteps
            && List.map (fun (output: RawMailboxOutputSlice) -> output.TypeIndex) successfulBegin.Outputs = mailbox.Entries[1].OutputTypeIndexes
            && (successfulBegin.Outputs |> List.forall (fun (output: RawMailboxOutputSlice) -> output.Reserved = 0u)))
        let malformedBegin = invokeRawMailboxEntry mailbox 1 [ malformedState; completion ]
        check ($"{optimization} malformed refined Bool state is rejected before mailbox output commit") (
            malformedBegin.Status <> 0
            && (malformedBegin.Diagnostic |> Option.exists (fun diagnostic -> diagnostic.EntryRole = Some "begin"))
            && malformedBegin.Context.CursorBytes = 0u
            && malformedBegin.Context.StepsConsumed = 0u
            && (malformedBegin.Outputs |> List.forall (fun (output: RawMailboxOutputSlice) -> output.TypeIndex = UInt32.MaxValue && output.OffsetBytes = 0u && output.OwnerEndBytes = 0u && output.Reserved = 0u))
            && (malformedBegin.StackBytes |> Array.forall ((=) 0uy)))

        let semanticReturn = invokeRawMailboxEntry mailbox 2 [ badCanonicalState; mailboxIntBytes 1L; completion ]
        check ($"{optimization} RETURN resume revalidates a canonical but semantically false Bool root (status={semanticReturn.Status}, diagnostic={semanticReturn.Diagnostic |> Option.map (fun diagnostic -> diagnostic.EntryRole, diagnostic.Code)}, steps={semanticReturn.Context.StepsConsumed}/{expectedValidatorSteps}, cursor={semanticReturn.Context.CursorBytes}/{expectedSemanticReturnCursor}, outputs={semanticReturn.Outputs |> List.map (fun (output: RawMailboxOutputSlice) -> output.TypeIndex, output.OffsetBytes, output.OwnerEndBytes, output.Reserved)})") (
            semanticReturn.Status <> 0
            && (semanticReturn.Diagnostic |> Option.exists (fun diagnostic -> diagnostic.EntryRole = Some "resume" && diagnostic.Code = "REFINEMENT_FAILED"))
            && semanticReturn.Context.StepsConsumed = expectedValidatorSteps
            && semanticReturn.Context.CursorBytes = uint32 expectedSemanticReturnCursor
            && (semanticReturn.Outputs |> List.forall (fun (output: RawMailboxOutputSlice) -> output.TypeIndex = UInt32.MaxValue && output.OffsetBytes = 0u && output.OwnerEndBytes = 0u && output.Reserved = 0u)))

        use resumeOwner = IrInterpreter.executeBodyWithInputs (noOpHost ()) "owning-refined-bool-mailbox-resume-owner" resumeOwnerBody None []
        let _, expectedResumeSteps =
            interpreterResultWithOwnerAndSteps
                "owning-refined-bool-mailbox-resume"
                resumeStepModel
                (Some resumeOwner)
                [ IrEntryArgument.RetainedRoot 0; IrEntryArgument.RetainedRoot 1; IrEntryArgument.UnitArgument ]
        let successfulReturn = invokeRawMailboxEntry mailbox 2 [ goodState; mailboxIntBytes 1L; completion ]
        check ($"{optimization} valid RETURN imports and executes the frozen resume body once") (
            successfulReturn.Status = 0
            && successfulReturn.Context.StepsConsumed = uint32 expectedResumeSteps + expectedValidatorSteps
            && successfulReturn.Outputs.Length = 1
            && successfulReturn.Outputs.Head.TypeIndex = mailbox.Entries[2].OutputTypeIndexes.Head
            && successfulReturn.Outputs.Head.OffsetBytes = 0u
            && successfulReturn.Outputs.Head.OwnerEndBytes = 8u
            && successfulReturn.Outputs.Head.Reserved = 0u)

        let associatedFailure, associatedRetry =
            invokeRawMailboxAssociatedResumeWithRetry mailbox successfulBegin malformedState badCanonicalState completion
        let parkedCursor = successfulBegin.Context.CursorBytes
        let parkedState = successfulBegin.Outputs[0]
        let parkedContinuation = successfulBegin.Outputs[1]
        let stateFieldOffset =
            parkedState.OffsetBytes
            + uint32 ((mailbox.Layouts |> List.find (fun layout -> layout.Type = mailbox.Entries[2].InputTypes[0])).Fields.Head.OffsetBytes)
        let retainedShape (invocation: RawMailboxInvocation) =
            invocation.RetainedInputs
            |> List.map (fun descriptor -> descriptor.TypeIndex, descriptor.OffsetBytes, descriptor.OwnerEndBytes, descriptor.Reserved)
        let expectedRetainedShape = [ parkedState; parkedContinuation ] |> List.map (fun descriptor -> descriptor.TypeIndex, descriptor.OffsetBytes, descriptor.OwnerEndBytes, descriptor.Reserved)
        let stateEnd = int (parkedState.OffsetBytes + (parkedState.OwnerEndBytes - parkedState.OffsetBytes)) - 1
        let continuationStart = int parkedContinuation.OffsetBytes
        let continuationEnd = int parkedContinuation.OwnerEndBytes - 1
        let sentinelStart = int parkedCursor
        let sentinelEnd = sentinelStart + completion.Length - 1
        let expectedContinuationBytes = successfulBegin.StackBytes[continuationStart..continuationEnd]
        let expectedParkedTail = Array.create completion.Length 0xCDuy
        let expectedRetryCursor = parkedCursor + uint32 completion.Length
        check ($"{optimization} KEEP rejects malformed retained Bool bytes before appending completion (status={associatedFailure.Status}, steps={associatedFailure.Context.StepsConsumed}, cursor={associatedFailure.Context.CursorBytes}/{parkedCursor}, outputs={associatedFailure.Outputs |> List.map (fun (output: RawMailboxOutputSlice) -> output.TypeIndex, output.OffsetBytes, output.OwnerEndBytes, output.Reserved)}, state={Convert.ToHexString(associatedFailure.StackBytes[int stateFieldOffset..(int stateFieldOffset + 7)])}, continuation={Convert.ToHexString(associatedFailure.StackBytes[continuationStart..continuationEnd])}, tail={Convert.ToHexString(associatedFailure.StackBytes[sentinelStart..sentinelEnd])}, retained={retainedShape associatedFailure})") (
            associatedFailure.Status <> 0 && associatedFailure.Context.CursorBytes = parkedCursor
            && associatedFailure.Context.StepsConsumed = 0u
            && (associatedFailure.Outputs |> List.forall (fun (output: RawMailboxOutputSlice) -> output.TypeIndex = UInt32.MaxValue && output.OffsetBytes = 0u && output.OwnerEndBytes = 0u && output.Reserved = 0u))
            && associatedFailure.StackBytes[int stateFieldOffset..stateEnd] = malformedState
            && associatedFailure.StackBytes[continuationStart..continuationEnd] = expectedContinuationBytes
            && associatedFailure.StackBytes[sentinelStart..sentinelEnd] = expectedParkedTail
            && retainedShape associatedFailure = expectedRetainedShape)
        check ($"{optimization} KEEP retry preserves its parked roots and skips semantic validator replay (status={associatedRetry.Status}/{associatedRetry.Context.Status}, cursor={associatedRetry.Context.CursorBytes}/{expectedRetryCursor}, steps={associatedRetry.Context.StepsConsumed}/{expectedNoReplaySteps}, outputs={associatedRetry.Outputs |> List.map (fun (output: RawMailboxOutputSlice) -> output.TypeIndex, output.OffsetBytes, output.OwnerEndBytes, output.Reserved)}, state={Convert.ToHexString(associatedRetry.StackBytes[int stateFieldOffset..stateEnd])}, continuation={Convert.ToHexString(associatedRetry.StackBytes[continuationStart..continuationEnd])}, retained={retainedShape associatedRetry})") (
            associatedRetry.Status = 0 && associatedRetry.Context.Status = 0u
            && associatedRetry.Context.CursorBytes = expectedRetryCursor
            && associatedRetry.Context.StepsConsumed = expectedNoReplaySteps
            && associatedRetry.Outputs.Length = 1
            && associatedRetry.Outputs.Head.TypeIndex = mailbox.Entries[2].OutputTypeIndexes.Head
            && associatedRetry.Outputs.Head.OffsetBytes = parkedState.OffsetBytes
            && associatedRetry.Outputs.Head.OwnerEndBytes = parkedState.OwnerEndBytes
            && associatedRetry.Outputs.Head.Reserved = 0u
            && associatedRetry.StackBytes[int stateFieldOffset..stateEnd] = badCanonicalState
            && associatedRetry.StackBytes[continuationStart..continuationEnd] = expectedContinuationBytes
            && associatedRetry.StackBytes[int parkedCursor..(int expectedRetryCursor - 1)] = completion
            && retainedShape associatedRetry = expectedRetainedShape)

let private testOwningRefinedIntSlice () =
    let toolchain = LlvmToolchain.discover()
    let withScalarConstructorSpan (context: Compiler.IrLoweringContext) scalarName constructorName =
        let scalarSpan = context.Scalars[scalarName].Span
        let constructor = context.Words[constructorName]
        let definition = { constructor.Definition with Span = scalarSpan }
        { context with Words = Map.add constructorName { constructor with Definition = definition } context.Words }
    let owningException action =
        try
            action ()
            failwith "Expected an OwningStackExecutionException."
        with :? OwningStackExecutionException as error -> error
    let owningError action = (owningException action).Diagnostic
    let withInterpreterSources (sources: NativeDiagnosticSources) =
        { noOpHost () with
            WordDefinitionSpan = fun word -> sources.WordDefinitionSpans.TryFind word
            PrimitiveDefinitionSpan = fun word -> sources.PrimitiveDefinitionSpans.TryFind word }
    let positiveValidator =
        wordEntry "is-positive?" [ TInt ] [ TBool ] Set.empty [
            Push(LInt 0L, span "owning-positive-validator.agent" 1)
            Call("int.greater-than", span "owning-positive-validator.agent" 2)
        ]
    let context =
        contextWithScalarDefinitions [ positiveValidator ] [
            scalarDefinition "PositiveId" TInt (Some "is-positive?"), "PositiveId.construct", "PositiveId.unwrap"
            scalarDefinition "OrderId" TInt None, "OrderId.create", "OrderId.raw"
        ]
        |> fun context -> withScalarConstructorSpan context "PositiveId" "PositiveId.construct"
    let sources = NativeDiagnosticSources.fromLoweringContext context
    let constructorBody = compileBodyWithInputs context "owning-refined-constructor" [ TInt ] [
        Call("PositiveId.construct", span "owning-refined-constructor.agent" 1)
    ]
    let identityBody = compileBodyWithInputs context "owning-refined-input-only" [ TNamed "PositiveId"; TNamed "OrderId" ] []
    let rejectingPositiveValidator =
        { positiveValidator with
            Definition =
                { positiveValidator.Definition with
                    Body = [ Call("drop", span "replacement-positive.agent" 1); Push(LBool false, span "replacement-positive.agent" 2) ]
                    Revision = 2 }
            Revision = 2 }
    let inputBody = compileBodyWithInputs context "owning-refined-reject-before-body" [ TNamed "PositiveId" ] [
        Call("drop", span "owning-refined-reject-before-body.agent" 1)
        Push(LInt 1L, span "owning-refined-reject-before-body.agent" 2)
        Push(LInt 0L, span "owning-refined-reject-before-body.agent" 3)
        Call("divide", span "owning-refined-reject-before-body.agent" 4)
    ]
    for optimization in [ LlvmOptimization.O0; LlvmOptimization.O2 ] do
        use constructor = compileOwningNative toolchain "owning-refined-constructor" optimization constructorBody
        let valid = constructor.Execute([ IntValue 1L ], 4096, 8)
        check ($"{optimization} PositiveId(1) constructor succeeds") (
            valid.Values = [ NamedValue("PositiveId", IntValue 1L) ]
            && Convert.ToHexString(valid.RetainedOutputBytes).ToLowerInvariant() = "0100000000000000")

        for invalidValue in [ 0L; -1L ] do
            let retained = Array.create 8 0xA5uy
            let nativeError = owningException (fun () -> constructor.ExecuteInto([ IntValue invalidValue ], 4096, retained) |> ignore)
            let diagnostic = nativeError.Diagnostic
            let interpreted =
                errorOf (fun () ->
                    use result =
                        IrInterpreter.executeBodyWithInputs
                            (withInterpreterSources sources)
                            "owning-refined-constructor"
                            constructorBody
                            None
                            [ IrEntryArgument.IntArgument invalidValue ]
                    result.Decode() |> ignore)
            check ($"{optimization} PositiveId constructor rejects {invalidValue}") (
                diagnostic = interpreted
                && diagnostic.Code = "REFINEMENT_FAILED"
                && diagnostic.Word = Some "PositiveId.construct"
                && diagnostic.Expected = [ "validator returns true" ]
                && diagnostic.Actual = [ "false" ])
            check ($"{optimization} false constructor leaves ExecuteInto bytes unchanged at {invalidValue}") (
                retained = Array.create 8 0xA5uy
                && nativeError.Metrics.FinalCursorBytes = 0
                && nativeError.Metrics.HostRetainedCommitBytes = 0)

        use identity = compileOwningNative toolchain "owning-refined-input-only" optimization identityBody
        let positiveLayout = identity.Layouts |> List.find (fun layout -> layout.TypeName = "PositiveId")
        let orderLayout = identity.Layouts |> List.find (fun layout -> layout.TypeName = "OrderId")
        check ($"{optimization} refined and unvalidated Int layouts remain distinct") (
            positiveLayout.Type <> orderLayout.Type
            && positiveLayout.PayloadBytes = 8 && positiveLayout.ExtentBytes = 8
            && orderLayout.PayloadBytes = 8 && orderLayout.ExtentBytes = 8)
        let validHost = identity.Execute([ NamedValue("PositiveId", IntValue 7L); NamedValue("OrderId", IntValue -4L) ], 4096, 16)
        check ($"{optimization} input-only validator closure admits valid exact nominal values") (
            validHost.Values = [ NamedValue("PositiveId", IntValue 7L); NamedValue("OrderId", IntValue -4L) ]
            && Convert.ToHexString(validHost.RetainedOutputBytes).ToLowerInvariant() = "0700000000000000fcffffffffffffff")
        let replacementContext =
            contextWithScalarDefinitions [ rejectingPositiveValidator ] [
                scalarDefinition "PositiveId" TInt (Some "is-positive?"), "PositiveId.construct", "PositiveId.unwrap"
                scalarDefinition "OrderId" TInt None, "OrderId.create", "OrderId.raw"
            ]
            |> fun replacement -> withScalarConstructorSpan replacement "PositiveId" "PositiveId.construct"
        let replacementIdentityBody = compileBodyWithInputs replacementContext "owning-refined-replacement-input-only" [ TNamed "PositiveId"; TNamed "OrderId" ] []
        use replacement = compileOwningNative toolchain "owning-refined-replacement-input-only" optimization replacementIdentityBody
        let replacementFailure =
            owningException (fun () -> replacement.Execute([ NamedValue("PositiveId", IntValue 7L); NamedValue("OrderId", IntValue -4L) ], 4096, 16) |> ignore)
        check ($"{optimization} owning snapshots retain the frozen validator revision after a same-name replacement") (
            replacementFailure.Diagnostic.Code = "REFINEMENT_FAILED"
            && replacementFailure.Diagnostic.Word = Some "PositiveId.construct")
        let oldSnapshotAfterReplacement = identity.Execute([ NamedValue("PositiveId", IntValue 7L); NamedValue("OrderId", IntValue -4L) ], 4096, 16)
        check ($"{optimization} an existing owning handle keeps its old frozen predicate after replacement compilation") (
            oldSnapshotAfterReplacement.Values = validHost.Values
            && oldSnapshotAfterReplacement.RetainedOutputBytes = validHost.RetainedOutputBytes)

        for label, badValue in [ "bare Int", IntValue 7L; "wrong nominal", NamedValue("OrderId", IntValue 7L) ] do
            let retained = Array.create 16 0xA5uy
            let mutable rejected = false
            try
                identity.ExecuteInto([ badValue; NamedValue("OrderId", IntValue 0L) ], 4096, retained) |> ignore
            with :? ArgumentException -> rejected <- true
            check ($"{optimization} host rejects {label} for a refined nominal input") rejected
            check ($"{optimization} {label} rejection leaves ExecuteInto bytes unchanged") (retained = Array.create 16 0xA5uy)

        use inputNative = compileOwningNative toolchain "owning-refined-reject-before-body" optimization inputBody
        let rejectedInput = Array.create 8 0xA5uy
        let invalidHostFailure =
            owningException (fun () ->
                inputNative.ExecuteInto([ NamedValue("PositiveId", IntValue 0L) ], 4096, rejectedInput) |> ignore)
        let invalidHostDiagnostic = invalidHostFailure.Diagnostic
        check ($"{optimization} invalid raw PositiveId is rejected before the body divide") (
            invalidHostDiagnostic.Code = "REFINEMENT_FAILED"
            && invalidHostDiagnostic.Word = Some "PositiveId.construct"
            && invalidHostDiagnostic.Expected = [ "validator returns true" ]
            && invalidHostDiagnostic.Actual = [ "false" ]
            && rejectedInput = Array.create 8 0xA5uy
            && invalidHostFailure.Metrics.FinalCursorBytes = 0
            && invalidHostFailure.Metrics.HostRetainedCommitBytes = 0)
        let validHostDiagnostic =
            owningError (fun () -> inputNative.Execute([ NamedValue("PositiveId", IntValue 1L) ], 4096, 8) |> ignore)
        check ($"{optimization} valid raw PositiveId reaches the failing body") (validHostDiagnostic.Code = "RUNTIME_DIVIDE_BY_ZERO")

    let runRefinedDepthCase label finalIndex expectSuccess =
        let deepWords =
            [ 0 .. finalIndex ]
            |> List.map (fun index ->
                let name = $"owning.refined.depth.{label}.{index}"
                let body =
                    if index < finalIndex then [ Call($"owning.refined.depth.{label}.{index + 1}", span (name + ".agent") 2) ]
                    else
                        [ Push(LInt 1L, span (name + ".agent") 2)
                          Call("PositiveId.construct", span (name + ".agent") 3)
                          Call("drop", span (name + ".agent") 4) ]
                wordEntry name [] [] Set.empty body)
        let depthContext =
            contextWithScalarDefinitions (positiveValidator :: deepWords) [
                scalarDefinition "PositiveId" TInt (Some "is-positive?"), "PositiveId.construct", "PositiveId.unwrap"
            ]
            |> fun value -> withScalarConstructorSpan value "PositiveId" "PositiveId.construct"
        let executionName = "owning-refined-depth-" + label
        let body = compileBody depthContext executionName [ Call($"owning.refined.depth.{label}.0", span "owning-refined-depth-entry.agent" 1) ]
        let interpreted =
            if expectSuccess then None
            else Some(errorOf (fun () -> interpreterResultWithSources (NativeDiagnosticSources.fromLoweringContext depthContext) executionName body |> ignore))
        for optimization in [ LlvmOptimization.O0; LlvmOptimization.O2 ] do
            use native = compileOwningNative toolchain ("owning-refined-depth-" + label) optimization body
            if expectSuccess then
                let result = native.Execute([], 4096, 8)
                check ($"{optimization} refined constructor permits validator at interpreter depth 64") result.Values.IsEmpty
            else
                let failure = owningException (fun () -> native.Execute([], 4096, 8) |> ignore)
                let interpretedError = interpreted |> Option.get
                let expectedConstructorCallSite = Some(span ($"owning.refined.depth.{label}.{finalIndex}.agent") 3)
                let expectedValidatorDefinition = Some(span "is-positive?.agent" 1)
                check ($"{optimization} refined constructor rejects validator at interpreter depth 65") (
                    failure.Diagnostic.Code = "RUNTIME_CALL_DEPTH"
                    && failure.Diagnostic.Word = Some "is-positive?"
                    && interpretedError.Code = failure.Diagnostic.Code
                    && interpretedError.Word = failure.Diagnostic.Word
                    && failure.Diagnostic.Span = expectedConstructorCallSite
                    && interpretedError.Span = expectedValidatorDefinition
                    && failure.Metrics.FinalCursorBytes = 0
                    && failure.Metrics.HostRetainedCommitBytes = 0)
    runRefinedDepthCase "depth-64" 62 true
    runRefinedDepthCase "depth-65" 63 false

    let runInlineDepthCase label finalIndex deepInputTypes deepOutputTypes deepBody entryPrefix scalarDefinitions =
        let deepWords =
            [ 0 .. finalIndex ]
            |> List.map (fun index ->
                let name = $"owning.inline.depth.{label}.{index}"
                let body =
                    if index < finalIndex then [ Call($"owning.inline.depth.{label}.{index + 1}", span (name + ".agent") 2) ]
                    else deepBody name
                wordEntry name deepInputTypes deepOutputTypes Set.empty body)
        let context =
            contextWithScalarDefinitions deepWords scalarDefinitions
        let executionName = "owning-inline-depth-" + label
        let body =
            compileBody context executionName (entryPrefix @ [ Call($"owning.inline.depth.{label}.0", span "owning-inline-depth-entry.agent" 1) ])
        let interpretedError =
            try
                use result = IrInterpreter.executeBodyWithInputs (withInterpreterSources (NativeDiagnosticSources.fromLoweringContext context)) executionName body None []
                result.Decode() |> ignore
                None
            with LanguageException diagnostic -> Some diagnostic
        for optimization in [ LlvmOptimization.O0; LlvmOptimization.O2 ] do
            use native = compileOwningNative toolchain ("owning-inline-depth-" + label) optimization body
            if finalIndex = 63 then
                let result = native.Execute([], 4096, 16)
                check ($"{optimization} {label} inline operation permits interpreter depth 64") (not result.Values.IsEmpty || deepOutputTypes.IsEmpty)
            else
                let failure = owningException (fun () -> native.Execute([], 4096, 16) |> ignore)
                let expected = interpretedError |> Option.get
                check ($"{optimization} {label} inline operation rejects interpreter depth 65") (
                    failure.Diagnostic.Code = "RUNTIME_CALL_DEPTH"
                    && failure.Diagnostic.Word = expected.Word
                    && failure.Metrics.FinalCursorBytes = 0
                    && failure.Metrics.HostRetainedCommitBytes = 0)

    runInlineDepthCase "unvalidated-wrap" 63 [] [] (fun name ->
        [ Push(LInt 1L, span (name + ".agent") 2)
          Call("PlainDepthId.create", span (name + ".agent") 3)
          Call("drop", span (name + ".agent") 4) ]) [] [ scalarDefinition "PlainDepthId" TInt None, "PlainDepthId.create", "PlainDepthId.raw" ]
    runInlineDepthCase "unvalidated-wrap-too-deep" 64 [] [] (fun name ->
        [ Push(LInt 1L, span (name + ".agent") 2)
          Call("PlainDepthId.create", span (name + ".agent") 3)
          Call("drop", span (name + ".agent") 4) ]) [] [ scalarDefinition "PlainDepthId" TInt None, "PlainDepthId.create", "PlainDepthId.raw" ]
    runInlineDepthCase "unvalidated-unwrap" 63 [ TNamed "PlainDepthId" ] [ TInt ] (fun name ->
        [ Call("PlainDepthId.raw", span (name + ".agent") 2) ])
        [ Push(LInt 1L, span "owning-inline-unwrap-entry.agent" 2); Call("PlainDepthId.create", span "owning-inline-unwrap-entry.agent" 3) ]
        [ scalarDefinition "PlainDepthId" TInt None, "PlainDepthId.create", "PlainDepthId.raw" ]
    runInlineDepthCase "unvalidated-unwrap-too-deep" 64 [ TNamed "PlainDepthId" ] [ TInt ] (fun name ->
        [ Call("PlainDepthId.raw", span (name + ".agent") 2) ])
        [ Push(LInt 1L, span "owning-inline-unwrap-entry.agent" 2); Call("PlainDepthId.create", span "owning-inline-unwrap-entry.agent" 3) ]
        [ scalarDefinition "PlainDepthId" TInt None, "PlainDepthId.create", "PlainDepthId.raw" ]
    runInlineDepthCase "primitive" 63 [] [] (fun name ->
        [ Push(LBool true, span (name + ".agent") 2)
          Call("bool.not", span (name + ".agent") 3)
          Call("drop", span (name + ".agent") 4) ]) [] []
    runInlineDepthCase "primitive-too-deep" 64 [] [] (fun name ->
        [ Push(LBool true, span (name + ".agent") 2)
          Call("bool.not", span (name + ".agent") 3)
          Call("drop", span (name + ".agent") 4) ]) [] []

    let rawHostDepthContext label finalFunctionIndex scalarName validatorName =
        let chainWords =
            [ 0 .. finalFunctionIndex ]
            |> List.map (fun index ->
                let name = $"owning.raw.host.depth.{label}.{index}"
                let body =
                    if index < finalFunctionIndex then
                        [ Call($"owning.raw.host.depth.{label}.{index + 1}", span (name + ".agent") 2) ]
                    else
                        [ Push(LBool true, span (name + ".agent") 2) ]
                wordEntry name [] [ TBool ] Set.empty body)
        let validator =
            wordEntry validatorName [ TInt ] [ TBool ] Set.empty [
                Call("drop", span (validatorName + ".agent") 2)
                Call($"owning.raw.host.depth.{label}.0", span (validatorName + ".agent") 3)
            ]
        contextWithScalarDefinitions (validator :: chainWords) [
            scalarDefinition scalarName TInt (Some validatorName), scalarName + ".construct", scalarName + ".unwrap"
        ]
        |> fun value -> withScalarConstructorSpan value scalarName (scalarName + ".construct")

    let runRawHostDepthCase label finalFunctionIndex scalarName validatorName expectSuccess =
        let depthContext = rawHostDepthContext label finalFunctionIndex scalarName validatorName
        let hostBodyName = "owning-raw-host-depth-" + label
        let hostBody = compileBodyWithInputs depthContext hostBodyName [ TNamed scalarName ] [ Call("drop", span (hostBodyName + ".agent") 1) ]
        let referenceBodyName = "owning-raw-host-depth-reference-" + label
        let referenceBody = compileBodyWithInputs depthContext referenceBodyName [ TInt ] [
            Call(validatorName, span (referenceBodyName + ".agent") 1)
            Call("drop", span (referenceBodyName + ".agent") 2)
        ]
        let sources = NativeDiagnosticSources.fromLoweringContext depthContext
        let executeReference () =
            use result =
                IrInterpreter.executeBodyWithInputs
                    (withInterpreterSources sources)
                    referenceBodyName
                    referenceBody
                    None
                    [ IrEntryArgument.IntArgument 1L ]
            result.Decode()
        if expectSuccess then
            let expected = executeReference ()
            check ($"interpreter raw-depth reference succeeds for {label}") expected.IsEmpty
            for optimization in [ LlvmOptimization.O0; LlvmOptimization.O2 ] do
                use native = compileOwningNative toolchain hostBodyName optimization hostBody
                let result = native.Execute([ NamedValue(scalarName, IntValue 1L) ], 8192, 8)
                check ($"{optimization} raw {label} admission allows the depth-64 call into depth 65") result.Values.IsEmpty
        else
            let expected = errorOf (fun () -> executeReference () |> ignore)
            let expectedCaller = $"owning.raw.host.depth.{label}.{finalFunctionIndex - 1}"
            let expectedTarget = $"owning.raw.host.depth.{label}.{finalFunctionIndex}"
            for optimization in [ LlvmOptimization.O0; LlvmOptimization.O2 ] do
                use native = compileOwningNative toolchain hostBodyName optimization hostBody
                let retained = Array.create 8 0xA5uy
                let failure =
                    owningException (fun () -> native.ExecuteInto([ NamedValue(scalarName, IntValue 1L) ], 8192, retained) |> ignore)
                check ($"{optimization} raw {label} validator rejects the first depth-65 call before the host body") (
                    failure.Diagnostic.Code = "RUNTIME_CALL_DEPTH"
                    && failure.Diagnostic.Word = Some expectedTarget
                    && failure.Diagnostic.Span = Some(span (expectedCaller + ".agent") 2)
                    && expected.Code = failure.Diagnostic.Code
                    && expected.Word = failure.Diagnostic.Word
                    && expected.Span = Some(span (expectedTarget + ".agent") 1)
                    && retained = Array.create 8 0xA5uy
                    && failure.Metrics.FinalCursorBytes = 0
                    && failure.Metrics.HostRetainedCommitBytes = 0)

    runRawHostDepthCase "depth-64" 63 "HostDepth64" "host-depth-64?" true
    runRawHostDepthCase "depth-65" 64 "HostDepth65" "host-depth-65?" false

    let fuelPredicate = wordEntry "fuel-predicate?" [ TInt ] [ TBool ] Set.empty [
        Call("drop", span "owning-fuel-predicate.agent" 1)
        Push(LBool true, span "owning-fuel-predicate.agent" 2)
    ]
    let fuelValidatorBody =
        [ 1 .. 4998 ]
        |> List.collect (fun _ -> [ Push(LInt 1L, span "owning-exact-fuel-validator.agent" 1); Call("drop", span "owning-exact-fuel-validator.agent" 2) ])
        |> fun prefix -> prefix @ [ Call("fuel-predicate?", span "owning-exact-fuel-validator.agent" 3) ]
    let fuelValidator = wordEntry "fuel-positive?" [ TInt ] [ TBool ] Set.empty fuelValidatorBody
    let fuelContext =
        contextWithScalarDefinitions [ fuelPredicate; fuelValidator ] [
            scalarDefinition "FuelId" TInt (Some "fuel-positive?"), "FuelId.construct", "FuelId.unwrap"
        ]
        |> fun value -> withScalarConstructorSpan value "FuelId" "FuelId.construct"
    let fuelBody = compileBodyWithInputs fuelContext "owning-exact-fuel-constructor" [ TInt ] [ Call("FuelId.construct", span "owning-exact-fuel-constructor.agent" 1) ]
    let mutable interpreterFuelSteps = 0
    let interpretedFuel =
        use result =
            IrInterpreter.executeBodyWithInputs
                { noOpHost () with ChargeInstruction = fun _ _ -> interpreterFuelSteps <- interpreterFuelSteps + 1 }
                "owning-exact-fuel-constructor"
                fuelBody
                None
                [ IrEntryArgument.IntArgument 1L ]
        result.Decode()
    check "interpreter constructor plus validator consumes exactly 10,000 steps" (
        interpreterFuelSteps = 10000
        && interpretedFuel = [ NamedValue("FuelId", IntValue 1L) ])
    for optimization in [ LlvmOptimization.O0; LlvmOptimization.O2 ] do
        use exactFuel = compileOwningNative toolchain "owning-exact-fuel-constructor" optimization fuelBody
        let result = exactFuel.Execute([ IntValue 1L ], 131072, 8)
        check ($"{optimization} constructor validator succeeds at the exact 10,000-step boundary") (
            result.Values = [ NamedValue("FuelId", IntValue 1L) ]
            && result.RetainedBytesWritten = 8)

    let overrunFuelBody = compileBodyWithInputs fuelContext "owning-overrun-fuel-constructor" [ TInt ] [
        Call("FuelId.construct", span "owning-overrun-fuel-constructor.agent" 1)
        Call("drop", span "owning-overrun-fuel-constructor.agent" 2)
    ]
    let mutable overrunFuelSteps = 0
    let interpretedOverrun =
        errorOf (fun () ->
            use result =
                IrInterpreter.executeBodyWithInputs
                    { noOpHost () with ChargeInstruction = fun _ _ -> overrunFuelSteps <- overrunFuelSteps + 1 }
                    "owning-overrun-fuel-constructor"
                    overrunFuelBody
                    None
                    [ IrEntryArgument.IntArgument 1L ]
            result.Decode() |> ignore)
    check "interpreter reports the first instruction beyond constructor-validator fuel" (
        overrunFuelSteps = 10001 && interpretedOverrun.Code = "RUNTIME_STEP_LIMIT")
    for optimization in [ LlvmOptimization.O0; LlvmOptimization.O2 ] do
        use overrunFuel = compileOwningNative toolchain "owning-overrun-fuel-constructor" optimization overrunFuelBody
        let failure = owningException (fun () -> overrunFuel.Execute([ IntValue 1L ], 131072, 8) |> ignore)
        check ($"{optimization} constructor validator charges the Wrap at the 10,001-step rejection boundary") (
            failure.Diagnostic = interpretedOverrun
            && failure.Metrics.FinalCursorBytes = 0
            && failure.Metrics.HostRetainedCommitBytes = 0)

    let overflowValidator =
        wordEntry "checked-positive?" [ TInt ] [ TBool ] Set.empty [
            Push(LInt 1L, span "owning-overflow-validator.agent" 1)
            Call("add", span "owning-overflow-validator.agent" 2)
            Push(LInt 0L, span "owning-overflow-validator.agent" 3)
            Call("int.greater-than", span "owning-overflow-validator.agent" 4)
        ]
    let overflowContext =
        contextWithScalarDefinitions [ overflowValidator ] [
            scalarDefinition "CheckedId" TInt (Some "checked-positive?"), "CheckedId.construct", "CheckedId.unwrap"
        ]
    let overflowInputBody = compileBodyWithInputs overflowContext "owning-refined-overflow-input" [ TNamed "CheckedId" ] []
    for optimization in [ LlvmOptimization.O0; LlvmOptimization.O2 ] do
        use checkedInput = compileOwningNative toolchain "owning-refined-overflow-input" optimization overflowInputBody
        let failure = owningException (fun () -> checkedInput.Execute([ NamedValue("CheckedId", IntValue Int64.MaxValue) ], 4096, 8) |> ignore)
        let diagnostic = failure.Diagnostic
        check ($"{optimization} raw host validator overflow propagates unchanged") (
            diagnostic.Code = "RUNTIME_OVERFLOW"
            && diagnostic.Word = Some "add"
            && diagnostic.Message.Contains("add", StringComparison.Ordinal)
            && failure.Metrics.FinalCursorBytes = 0
            && failure.Metrics.HostRetainedCommitBytes = 0)

    let envelope = recordDefinition "OwnerEnvelope" [ recordField "owner" (TNamed "PositiveId") ]
    let nestedContext =
        contextWithRecordDefinitions [ positiveValidator ] [ envelope ] [
            scalarDefinition "PositiveId" TInt (Some "is-positive?"), "PositiveId.construct", "PositiveId.unwrap"
            scalarDefinition "OrderId" TInt None, "OrderId.create", "OrderId.raw"
        ]
    let nestedTypes = [
        TNamed "OwnerEnvelope"
        TOption(TNamed "PositiveId")
        TOption(TNamed "PositiveId")
        TResult(TNamed "PositiveId", TInt)
        TResult(TNamed "PositiveId", TInt)
        TResult(TInt, TNamed "PositiveId")
        TResult(TInt, TNamed "PositiveId")
    ]
    let nestedBody = compileBodyWithInputs nestedContext "owning-refined-nested-inputs" nestedTypes []
    let positive number = NamedValue("PositiveId", IntValue number)
    let nestedValues = [
        RecordValue("OwnerEnvelope", Map.ofList [ "owner", positive 1L ])
        OptionValue(TNamed "PositiveId", Some(positive 2L))
        OptionValue(TNamed "PositiveId", None)
        ResultValue(TNamed "PositiveId", TInt, Ok(positive 3L))
        ResultValue(TNamed "PositiveId", TInt, Error(IntValue 4L))
        ResultValue(TInt, TNamed "PositiveId", Ok(IntValue 5L))
        ResultValue(TInt, TNamed "PositiveId", Error(positive 6L))
    ]
    let replaceAt index replacement values =
        values |> List.mapi (fun current value -> if current = index then replacement else value)
    for optimization in [ LlvmOptimization.O0; LlvmOptimization.O2 ] do
        use nested = compileOwningNative toolchain "owning-refined-nested-inputs" optimization nestedBody
        let actual = nested.Execute(nestedValues, 8192, 256)
        check ($"{optimization} refined nested record, Option, and active Result cases roundtrip") (actual.Values = nestedValues)
        for index, invalidValue in [
            0, RecordValue("OwnerEnvelope", Map.ofList [ "owner", positive 0L ])
            1, OptionValue(TNamed "PositiveId", Some(positive -1L))
            3, ResultValue(TNamed "PositiveId", TInt, Ok(positive 0L))
            6, ResultValue(TInt, TNamed "PositiveId", Error(positive -1L))
        ] do
            let retained = Array.create 256 0xA5uy
            let diagnostic = owningError (fun () -> nested.ExecuteInto(replaceAt index invalidValue nestedValues, 8192, retained) |> ignore)
            check ($"{optimization} nested active refined value at input {index} is rejected") (
                diagnostic.Code = "REFINEMENT_FAILED"
                && diagnostic.Word = Some "PositiveId.construct"
                && retained = Array.create 256 0xA5uy)

    let effectfulValidator =
        wordEntry "effectful-positive?" [ TInt ] [ TBool ] (Set.singleton "console.write") [
            Push(LInt 0L, span "owning-effectful-validator.agent" 1)
            Call("int.greater-than", span "owning-effectful-validator.agent" 2)
        ]
    let effectfulContext =
        contextWithScalarDefinitions [ effectfulValidator ] [
            scalarDefinition "EffectfulId" TInt (Some "effectful-positive?"), "EffectfulId.construct", "EffectfulId.unwrap"
        ]
    let effectfulError = errorOf (fun () ->
        let effectfulBody = compileBodyWithInputs effectfulContext "owning-effectful-refinement" [ TNamed "EffectfulId" ] []
        compileOwningNative toolchain "owning-effectful-refinement" LlvmOptimization.O0 effectfulBody |> ignore)
    check "the verified program rejects an effectful Int validator before owning compilation" (
        effectfulError.Code = "TYPE_VALIDATOR_EFFECT"
        && effectfulError.Word = Some "EffectfulId"
        && effectfulError.Actual = [ "console.write" ])

    let unsupportedBranchValidator =
        wordEntry "unsupported-branch?" [ TInt ] [ TBool ] Set.empty [
            Push(LBool false, span "owning-unsupported-validator.agent" 1)
            If(
                [ Call("drop", span "owning-unsupported-validator.agent" 2)
                  Push(LFloat 1.5, span "owning-unsupported-validator.agent" 3)
                  Call("drop", span "owning-unsupported-validator.agent" 4)
                  Push(LBool true, span "owning-unsupported-validator.agent" 5) ],
                [ Call("drop", span "owning-unsupported-validator.agent" 6)
                  Push(LBool false, span "owning-unsupported-validator.agent" 7) ],
                span "owning-unsupported-validator.agent" 1)
        ]
    let unsupportedBranchContext =
        contextWithScalarDefinitions [ unsupportedBranchValidator ] [
            scalarDefinition "UntakenBranchId" TInt (Some "unsupported-branch?"), "UntakenBranchId.construct", "UntakenBranchId.unwrap"
        ]
    let unsupportedBranchBody = compileBodyWithInputs unsupportedBranchContext "owning-validator-type-only-closure" [ TNamed "UntakenBranchId" ] []
    let unsupportedBranch = errorOf (fun () -> compileOwningNative toolchain "owning-validator-type-only-closure" LlvmOptimization.O0 unsupportedBranchBody |> ignore)
    check "validator-only closure scans unsupported IR in an untaken branch" (
        unsupportedBranch.Code = "IR_OWNING_STACK_CONSTANT_UNSUPPORTED"
        && unsupportedBranch.Span = Some(span "owning-unsupported-validator.agent" 3))

    let missingConstructorContext =
        { unsupportedBranchContext with
            Words = Map.remove "UntakenBranchId.construct" unsupportedBranchContext.Words
            WordIds = Map.remove "UntakenBranchId.construct" unsupportedBranchContext.WordIds }
    let missingConstructorBody = compileBodyWithInputs missingConstructorContext "owning-refined-missing-constructor" [ TNamed "UntakenBranchId" ] []
    let missingConstructor = errorOf (fun () -> compileOwningNative toolchain "owning-refined-missing-constructor" LlvmOptimization.O0 missingConstructorBody |> ignore)
    check "refined input-only layout rejects a missing generated diagnostic constructor during preflight" (
        missingConstructor.Code = "IR_OWNING_STACK_SCALAR_CONSTRUCTOR_MISSING"
        && missingConstructor.Word = Some "owning-refined-missing-constructor")

    let duplicateConstructorName = "UntakenBranchId.alternate-construct"
    let duplicateConstructor =
        generatedScalarEntry duplicateConstructorName (ScalarConstructor "UntakenBranchId") [ TInt ] [ TNamed "UntakenBranchId" ]
    let duplicateConstructorContext =
        { unsupportedBranchContext with
            Words = Map.add duplicateConstructorName duplicateConstructor unsupportedBranchContext.Words
            WordIds = Map.add duplicateConstructorName (WordId("generated-" + duplicateConstructorName)) unsupportedBranchContext.WordIds }
    let duplicateConstructorOutcome =
        try
            let body = compileBodyWithInputs duplicateConstructorContext "owning-refined-ambiguous-constructor" [ TNamed "UntakenBranchId" ] []
            let diagnostic = errorOf (fun () -> compileOwningNative toolchain "owning-refined-ambiguous-constructor" LlvmOptimization.O0 body |> ignore)
            Some diagnostic.Code
        with LanguageException diagnostic -> Some diagnostic.Code
    check "multiple generated WrapScalar diagnostics targets are rejected when IR permits them" (
        duplicateConstructorOutcome = Some "IR_OWNING_STACK_SCALAR_CONSTRUCTOR_AMBIGUOUS")

    let mailboxContext =
        contextWithRecordDefinitions [ positiveValidator ] [
            recordDefinition "MailboxState" [ recordField "owner" (TNamed "PositiveId") ]
            recordDefinition "MailboxContinuation" [ recordField "marker" TInt ]
        ] [
            scalarDefinition "PositiveId" TInt (Some "is-positive?"), "PositiveId.construct", "PositiveId.unwrap"
        ]
    let mailboxProgram = Compiler.compileIrProgram mailboxContext
    let initialize = Compiler.compileIrBodyAgainstProgram mailboxContext mailboxProgram "owning-refined-mailbox-initialize" [ TString ] [
        Call("drop", span "owning-refined-mailbox.agent" 1)
        Push(LInt 1L, span "owning-refined-mailbox.agent" 2)
        Call("PositiveId.construct", span "owning-refined-mailbox.agent" 3)
        Call("mailboxState.new", span "owning-refined-mailbox.agent" 4)
    ]
    let beginTurn = Compiler.compileIrBodyAgainstProgram mailboxContext mailboxProgram "owning-refined-mailbox-begin" [ TNamed "MailboxState"; TString ] [
        Call("drop", span "owning-refined-mailbox.agent" 5)
        Push(LInt 1L, span "owning-refined-mailbox.agent" 6)
        Call("mailboxContinuation.new", span "owning-refined-mailbox.agent" 7)
    ]
    let resume = Compiler.compileIrBodyAgainstProgram mailboxContext mailboxProgram "owning-refined-mailbox-resume" [ TNamed "MailboxState"; TNamed "MailboxContinuation"; TString ] [
        Call("drop", span "owning-refined-mailbox.agent" 8)
        Call("drop", span "owning-refined-mailbox.agent" 9)
    ]
    let mailbox =
        OwningStackAot.compileMailboxWithProfile
            toolchain
            LlvmOptimization.O0
            OwningRuntimeProfile.Diagnostic
            (Path.Combine(artifactRoot, "owning-refined-mailbox-int"))
            initialize
            beginTurn
            resume
    check "mailbox layout admits a nested refined Int scalar" (
        mailbox.Entries.Length = 3
        && (mailbox.Layouts |> List.exists (fun layout -> layout.TypeName = "PositiveId")))
    check "mailbox begin validates refined Int inputs before the entry frame" (
        mailbox.LlvmIr.Contains("mailbox.validation.frame.entered", StringComparison.Ordinal)
        && mailbox.LlvmIr.Contains("@agentlang_mailbox_begin_frame", StringComparison.Ordinal))
    check "mailbox callback metadata bound includes the refined admission allocas" (
        (mailbox.CallbackMetadataPerEntryBytes.TryFind "begin" |> Option.exists (fun bytes -> bytes > 0))
        && mailbox.BackendMetadataPeakBoundBytes >= int64 (mailbox.CallbackMetadataPerEntryBytes["begin"]))
    let mailboxBeginOutputTypeIndexes: uint32 list = mailbox.Entries[1].OutputTypeIndexes
    let validMailboxTurn = invokeRawMailboxEntry mailbox 1 [ mailboxIntBytes 1L; mailboxStringBytes "turn" ]
    check "valid refined Int mailbox input executes and publishes both begin outputs" (
        validMailboxTurn.Status = 0
        && List.map (fun (output: RawMailboxOutputSlice) -> output.TypeIndex) validMailboxTurn.Outputs = mailboxBeginOutputTypeIndexes
        && validMailboxTurn.Context.CallDepth = 0u)
    // The raw callback imports the 8-byte state and 16-byte turn once. In the
    // verified begin body, MailboxContinuation construction copies its one
    // 8-byte marker field. Admission only reads the imported root and adds no
    // payload copy or move.
    let expectedIntMailboxInputCopyBytes = uint64 (8 + (mailboxStringBytes "turn").Length)
    let expectedIntMailboxBodyDeepCopyBytes = 8UL
    check ($"refined Int mailbox admission preserves verified body counters (deep={validMailboxTurn.Context.DeepCopyBytes}, expected-deep={expectedIntMailboxBodyDeepCopyBytes}, move={validMailboxTurn.Context.MoveBytes}, input={validMailboxTurn.Context.InputCopyBytes}, expected-input={expectedIntMailboxInputCopyBytes})") (
        validMailboxTurn.Context.DeepCopyBytes = expectedIntMailboxBodyDeepCopyBytes
        && validMailboxTurn.Context.MoveBytes = 0UL
        && validMailboxTurn.Context.InputCopyBytes = expectedIntMailboxInputCopyBytes)
    let invalidMailboxTurn = invokeRawMailboxEntry mailbox 1 [ mailboxIntBytes 0L; mailboxStringBytes "turn" ]
    check "invalid nested refined Int input fails before begin publishes output" (
        invalidMailboxTurn.Status <> 0
        && (invalidMailboxTurn.Diagnostic |> Option.exists (fun diagnostic -> diagnostic.EntryRole = Some "begin" && diagnostic.Code = "REFINEMENT_FAILED"))
        && (invalidMailboxTurn.Outputs |> List.forall (fun output -> output.TypeIndex = UInt32.MaxValue && output.OffsetBytes = 0u && output.OwnerEndBytes = 0u)))

let private testOwningRefinedStringSlice () =
    let toolchain = LlvmToolchain.discover()
    let withScalarConstructorSpan (context: Compiler.IrLoweringContext) scalarName constructorName =
        let scalarSpan = context.Scalars[scalarName].Span
        let constructor = context.Words[constructorName]
        let definition = { constructor.Definition with Span = scalarSpan }
        { context with Words = Map.add constructorName { constructor with Definition = definition } context.Words }
    let owningException action =
        try
            action ()
            failwith "Expected an OwningStackExecutionException."
        with :? OwningStackExecutionException as error -> error
    let owningError action = (owningException action).Diagnostic
    let invokeRawOwningEntry (native: OwningStackCompiledProgram) (inputBytes: byte array) (inputExtents: int array) retainedCapacity =
        let library = NativeLibrary.Load native.LibraryPath
        let input = Marshal.AllocHGlobal(max 1 inputBytes.Length)
        let extents = Marshal.AllocHGlobal(max sizeof<int32> (inputExtents.Length * sizeof<int32>))
        let retained = Marshal.AllocHGlobal(max 1 retainedCapacity)
        let contextPointer = Marshal.AllocHGlobal(Marshal.SizeOf<NativeOwningContext>())
        let stackCapacity = 4096
        let bitmapBytes = stackCapacity / 8
        let stack = Marshal.AllocHGlobal stackCapacity
        let initialized = Marshal.AllocHGlobal bitmapBytes
        let poison = Marshal.AllocHGlobal bitmapBytes
        let traceCapacity = 8192
        let trace = Marshal.AllocHGlobal(traceCapacity * 40)
        try
            if inputBytes.Length > 0 then Marshal.Copy(inputBytes, 0, input, inputBytes.Length)
            if inputExtents.Length > 0 then Marshal.Copy(Array.zeroCreate<byte> (inputExtents.Length * sizeof<int32>), 0, extents, inputExtents.Length * sizeof<int32>)
            for index, extent in inputExtents |> List.ofArray |> List.indexed do
                Marshal.WriteInt32(extents, index * sizeof<int32>, extent)
            Marshal.Copy(Array.create retainedCapacity 0xA5uy, 0, retained, retainedCapacity)
            Marshal.Copy(Array.zeroCreate<byte> stackCapacity, 0, stack, stackCapacity)
            Marshal.Copy(Array.zeroCreate<byte> bitmapBytes, 0, initialized, bitmapBytes)
            Marshal.Copy(Array.zeroCreate<byte> bitmapBytes, 0, poison, bitmapBytes)
            Marshal.Copy(Array.zeroCreate<byte> (traceCapacity * 40), 0, trace, traceCapacity * 40)
            let mutable context = Unchecked.defaultof<NativeOwningContext>
            context.AbiVersion <- 1u
            context.StackCapacityBytes <- uint32 stackCapacity
            context.TraceEventCapacity <- uint32 traceCapacity
            context.InitBitmapBytes <- uint32 bitmapBytes
            context.StackData <- stack
            context.InitBitmap <- initialized
            context.PoisonBitmap <- poison
            context.TraceEvents <- trace
            Marshal.StructureToPtr(context, contextPointer, false)
            let execute = Marshal.GetDelegateForFunctionPointer<RawOwningExecuteDelegate>(NativeLibrary.GetExport(library, "agentlang_owning_execute"))
            let status = execute.Invoke(contextPointer, input, inputBytes.Length, extents, inputExtents.Length, retained, retainedCapacity)
            let finalContext = Marshal.PtrToStructure<NativeOwningContext>(contextPointer)
            let retainedBytes = Array.create retainedCapacity 0xA5uy
            Marshal.Copy(retained, retainedBytes, 0, retainedCapacity)
            status, finalContext, retainedBytes
        finally
            NativeLibrary.Free library
            Marshal.FreeHGlobal trace
            Marshal.FreeHGlobal poison
            Marshal.FreeHGlobal initialized
            Marshal.FreeHGlobal stack
            Marshal.FreeHGlobal contextPointer
            Marshal.FreeHGlobal retained
            Marshal.FreeHGlobal extents
            Marshal.FreeHGlobal input

    let nonEmptyValidator =
        wordEntry "is-non-empty?" [ TString ] [ TBool ] Set.empty [
            Call("string.length", span "owning-non-empty-validator.agent" 1)
            Push(LInt 0L, span "owning-non-empty-validator.agent" 2)
            Call("int.greater-than", span "owning-non-empty-validator.agent" 3)
        ]
    let singleContext =
        contextWithScalarDefinitions [ nonEmptyValidator ] [
            scalarDefinition "NonEmptyString" TString (Some "is-non-empty?"), "NonEmptyString.construct", "NonEmptyString.unwrap"
        ]
        |> fun context -> withScalarConstructorSpan context "NonEmptyString" "NonEmptyString.construct"
    let constructorBody = compileBodyWithInputs singleContext "owning-refined-string-constructor" [ TString ] [
        Call("NonEmptyString.construct", span "owning-refined-string-constructor.agent" 1)
    ]
    let emptyRawBytes = Array.zeroCreate<byte> 8
    let expectedStringBytes = "02000000000000006f006b0000000000"
    let expectedOtherStringBytes = "020000000000000079006f0000000000"
    let validString = NamedValue("NonEmptyString", StringValue "ok")
    for optimization in [ LlvmOptimization.O0; LlvmOptimization.O2 ] do
        use constructor = compileOwningNative toolchain "owning-refined-string-constructor" optimization constructorBody
        let nominalLayout = constructor.Layouts |> List.find (fun layout -> layout.TypeName = "NonEmptyString")
        let stringLayout = constructor.Layouts |> List.find (fun layout -> layout.Type = IrString)
        check ($"{optimization} refined String layout retains nominal key and dynamic String extent") (
            nominalLayout.Type = IrNominal(ProgramTypeKey 0)
            && nominalLayout.PayloadBytes = -1
            && nominalLayout.ExtentBytes = -1
            && nominalLayout.IsDynamic
            && nominalLayout.MinimumPayloadBytes = 8
            && nominalLayout.MinimumExtentBytes = 8
            && stringLayout.TypeName = "String"
            && stringLayout.IsDynamic
            && stringLayout.MinimumPayloadBytes = 8
            && stringLayout.MinimumExtentBytes = 8)
        check ($"{optimization} refined String uses String kind 5 with its nominal and String TypeIds") (
            constructor.LlvmIr.Contains("i32 5, i32 4, i32 0, i32 0, i32 4294967295, i32 4294967295, i32 8, i32 8, i32 0", StringComparison.Ordinal)
            && constructor.LlvmIr.Contains("i32 5, i32 5, i32 0, i32 0, i32 4294967295, i32 4294967295, i32 8, i32 8, i32 0", StringComparison.Ordinal))
        let constructed = constructor.Execute([ StringValue "ok" ], 4096, 16)
        check ($"{optimization} NonEmptyString constructor preserves UTF-16 String bytes and nominal identity") (
            constructed.Values = [ validString ]
            && constructed.RetainedBytesWritten = 16
            && Convert.ToHexString(constructed.RetainedOutputBytes).ToLowerInvariant() = expectedStringBytes
            && constructed.Metrics.DeepCopyBytes = 0UL
            && constructed.Metrics.MoveBytes = 0UL)
        let retained = Array.create 16 0xA5uy
        let emptyFailure = owningException (fun () -> constructor.ExecuteInto([ StringValue "" ], 4096, retained) |> ignore)
        check ($"{optimization} empty NonEmptyString construction fails atomically") (
            emptyFailure.Diagnostic.Code = "REFINEMENT_FAILED"
            && emptyFailure.Diagnostic.Word = Some "NonEmptyString.construct"
            && retained = Array.create 16 0xA5uy
            && emptyFailure.Metrics.FinalCursorBytes = 0
            && emptyFailure.Metrics.HostRetainedCommitBytes = 0)

    let identityContext =
        contextWithScalarDefinitions [ nonEmptyValidator ] [
            scalarDefinition "NonEmptyString" TString (Some "is-non-empty?"), "NonEmptyString.construct", "NonEmptyString.unwrap"
            scalarDefinition "OtherString" TString (Some "is-non-empty?"), "OtherString.construct", "OtherString.unwrap"
        ]
        |> fun context -> withScalarConstructorSpan context "NonEmptyString" "NonEmptyString.construct"
    let identityBody = compileBodyWithInputs identityContext "owning-refined-string-input-only" [ TNamed "NonEmptyString"; TNamed "OtherString" ] []
    let identityInput = [ validString; NamedValue("OtherString", StringValue "yo") ]
    for optimization in [ LlvmOptimization.O0; LlvmOptimization.O2 ] do
        use identity = compileOwningNative toolchain "owning-refined-string-input-only" optimization identityBody
        let nonEmptyLayout = identity.Layouts |> List.find (fun layout -> layout.TypeName = "NonEmptyString")
        let otherLayout = identity.Layouts |> List.find (fun layout -> layout.TypeName = "OtherString")
        check ($"{optimization} refined String nominals have distinct verified identities") (
            nonEmptyLayout.Type <> otherLayout.Type
            && nonEmptyLayout.IsDynamic && otherLayout.IsDynamic
            && nonEmptyLayout.MinimumPayloadBytes = 8 && otherLayout.MinimumPayloadBytes = 8)
        let identityResult = identity.Execute(identityInput, 4096, 32)
        check ($"{optimization} validator-only type dependencies admit and roundtrip exact nominal String values") (
            identityResult.Values = identityInput
            && Convert.ToHexString(identityResult.RetainedOutputBytes).ToLowerInvariant() = expectedStringBytes + expectedOtherStringBytes)
        for label, invalidFirst in [ "bare String", StringValue "ok"; "different nominal", NamedValue("OtherString", StringValue "ok") ] do
            let retained = Array.create 32 0xA5uy
            let mutable rejected = false
            try
                identity.ExecuteInto([ invalidFirst; identityInput[1] ], 4096, retained) |> ignore
            with :? ArgumentException -> rejected <- true
            check ($"{optimization} host rejects {label} for NonEmptyString") rejected
            check ($"{optimization} {label} rejection leaves caller bytes unchanged") (retained = Array.create 32 0xA5uy)

    let rawRejectBody = compileBodyWithInputs singleContext "owning-refined-string-reject-before-body" [ TNamed "NonEmptyString" ] [
        Call("drop", span "owning-refined-string-reject-before-body.agent" 1)
        Push(LInt 1L, span "owning-refined-string-reject-before-body.agent" 2)
        Push(LInt 0L, span "owning-refined-string-reject-before-body.agent" 3)
        Call("divide", span "owning-refined-string-reject-before-body.agent" 4)
    ]
    for optimization in [ LlvmOptimization.O0; LlvmOptimization.O2 ] do
        use rawInput = compileOwningNative toolchain "owning-refined-string-reject-before-body" optimization rawRejectBody
        let failure = owningError (fun () -> rawInput.Execute([ NamedValue("NonEmptyString", StringValue "") ], 4096, 8) |> ignore)
        check ($"{optimization} host encoded empty refined String fails refinement before divide") (
            failure.Code = "REFINEMENT_FAILED" && failure.Word = Some "NonEmptyString.construct")
        let status, context, rawRetained = invokeRawOwningEntry rawInput emptyRawBytes [| 8 |] 8
        check ($"{optimization} exported owning entry validates fixture String bytes before executing the body") (
            status = 1
            && context.Status = 1u
            && context.ErrorId > 0u
            && context.StepsConsumed = 3u
            && context.CursorBytes = 0u
            && context.RetainedCopyBytes = 0UL
            && rawRetained = Array.create 8 0xA5uy)
        let validRawBodyFailure = owningError (fun () -> rawInput.Execute([ validString ], 4096, 8) |> ignore)
        check ($"{optimization} valid raw NonEmptyString reaches the intentionally failing body") (validRawBodyFailure.Code = "RUNTIME_DIVIDE_BY_ZERO")

    let replacementValidator =
        { nonEmptyValidator with
            Definition =
                { nonEmptyValidator.Definition with
                    Body = [ Call("drop", span "replacement-non-empty.agent" 1); Push(LBool false, span "replacement-non-empty.agent" 2) ]
                    Revision = 2 }
            Revision = 2 }
    let replacementContext =
        contextWithScalarDefinitions [ replacementValidator ] [
            scalarDefinition "NonEmptyString" TString (Some "is-non-empty?"), "NonEmptyString.construct", "NonEmptyString.unwrap"
        ]
        |> fun context -> withScalarConstructorSpan context "NonEmptyString" "NonEmptyString.construct"
    let replacementBody = compileBodyWithInputs replacementContext "owning-refined-string-replacement" [ TNamed "NonEmptyString" ] []
    for optimization in [ LlvmOptimization.O0; LlvmOptimization.O2 ] do
        use original = compileOwningNative toolchain "owning-refined-string-frozen-original-single" optimization (compileBodyWithInputs singleContext "owning-refined-string-frozen-original-single" [ TNamed "NonEmptyString" ] [])
        use replacement = compileOwningNative toolchain "owning-refined-string-replacement" optimization replacementBody
        let replacementFailure = owningException (fun () -> replacement.Execute([ validString ], 4096, 16) |> ignore)
        check ($"{optimization} same-name String validator replacement retains the frozen revision") (
            replacementFailure.Diagnostic.Code = "REFINEMENT_FAILED"
            && replacementFailure.Diagnostic.Word = Some "NonEmptyString.construct")
        check ($"{optimization} original String validator snapshot remains executable after replacement") (
            original.Execute([ validString ], 4096, 16).Values = [ validString ])

    let envelope = recordDefinition "StringOwnerEnvelope" [
        recordField "owner" (TNamed "NonEmptyString")
        recordField "tail" TString
    ]
    let nestedContext =
        contextWithRecordDefinitions [ nonEmptyValidator ] [ envelope ] [
            scalarDefinition "NonEmptyString" TString (Some "is-non-empty?"), "NonEmptyString.construct", "NonEmptyString.unwrap"
        ]
        |> fun context -> withScalarConstructorSpan context "NonEmptyString" "NonEmptyString.construct"
    let nestedTypes = [
        TNamed "StringOwnerEnvelope"
        TOption(TNamed "NonEmptyString")
        TOption(TNamed "NonEmptyString")
        TResult(TNamed "NonEmptyString", TInt)
        TResult(TNamed "NonEmptyString", TInt)
        TResult(TInt, TNamed "NonEmptyString")
        TResult(TInt, TNamed "NonEmptyString")
    ]
    let nestedBody = compileBodyWithInputs nestedContext "owning-refined-string-nested-inputs" nestedTypes []
    let nominalString text = NamedValue("NonEmptyString", StringValue text)
    let nestedValues = [
        RecordValue("StringOwnerEnvelope", Map.ofList [ "owner", nominalString "ok"; "tail", StringValue "tail" ])
        OptionValue(TNamed "NonEmptyString", Some(nominalString "one"))
        OptionValue(TNamed "NonEmptyString", None)
        ResultValue(TNamed "NonEmptyString", TInt, Ok(nominalString "two"))
        ResultValue(TNamed "NonEmptyString", TInt, Error(IntValue 4L))
        ResultValue(TInt, TNamed "NonEmptyString", Ok(IntValue 5L))
        ResultValue(TInt, TNamed "NonEmptyString", Error(nominalString "three"))
    ]
    let replaceAt index replacement values =
        values |> List.mapi (fun current value -> if current = index then replacement else value)
    let unwrapFieldBody = compileBodyWithInputs nestedContext "owning-refined-string-owner-end" [ TNamed "StringOwnerEnvelope" ] [
        Call("stringOwnerEnvelope.owner", span "owning-refined-string-owner-end.agent" 1)
        Call("NonEmptyString.unwrap", span "owning-refined-string-owner-end.agent" 2)
    ]
    for optimization in [ LlvmOptimization.O0; LlvmOptimization.O2 ] do
        use nested = compileOwningNative toolchain "owning-refined-string-nested-inputs" optimization nestedBody
        let roundtrip = nested.Execute(nestedValues, 8192, 256)
        check ($"{optimization} refined String validates records, Option Some, and active Result payloads recursively") (roundtrip.Values = nestedValues)
        for index, invalidValue in [
            0, RecordValue("StringOwnerEnvelope", Map.ofList [ "owner", nominalString ""; "tail", StringValue "tail" ])
            1, OptionValue(TNamed "NonEmptyString", Some(nominalString ""))
            3, ResultValue(TNamed "NonEmptyString", TInt, Ok(nominalString ""))
            6, ResultValue(TInt, TNamed "NonEmptyString", Error(nominalString ""))
        ] do
            let retained = Array.create 256 0xA5uy
            let failure = owningError (fun () -> nested.ExecuteInto(replaceAt index invalidValue nestedValues, 8192, retained) |> ignore)
            check ($"{optimization} invalid active nested refined String {index} is rejected atomically") (
                failure.Code = "REFINEMENT_FAILED"
                && failure.Word = Some "NonEmptyString.construct"
                && retained = Array.create 256 0xA5uy)
        use unwrapField = compileOwningNative toolchain "owning-refined-string-owner-end" optimization unwrapFieldBody
        let ownerEndResult =
            unwrapField.Execute(
                [ RecordValue("StringOwnerEnvelope", Map.ofList [ "owner", nominalString "ok"; "tail", StringValue "tail" ]) ],
                4096,
                16)
        let stringTypeId = 6u
        let preservesOwnerEnd =
            ownerEndResult.LayoutEvents
            |> List.exists (fun event ->
                event.Kind = "descriptor-transfer"
                && event.TypeId = stringTypeId
                && event.OffsetBytes = 0
                && event.SourceOffsetBytes = Some 32
                && event.SourceExtentBytes = Some 16)
        check ($"{optimization} refined String field projection and unwrap preserve the trailing String owner range") (
            ownerEndResult.Values = [ StringValue "ok" ]
            && Convert.ToHexString(ownerEndResult.RetainedOutputBytes).ToLowerInvariant() = expectedStringBytes
            && ownerEndResult.Metrics.InputBytes = 32
            && ownerEndResult.Metrics.HostEncodedInputBytes = 32
            && ownerEndResult.Metrics.DeepCopyBytes = 0UL
            && ownerEndResult.Metrics.MoveBytes = 0UL
            && preservesOwnerEnd)

    let failingValidator =
        wordEntry "failing-string-validator?" [ TString ] [ TBool ] Set.empty [
            Call("drop", span "failing-string-validator.agent" 1)
            Push(LInt 1L, span "failing-string-validator.agent" 2)
            Push(LInt 0L, span "failing-string-validator.agent" 3)
            Call("divide", span "failing-string-validator.agent" 4)
            Push(LInt 0L, span "failing-string-validator.agent" 5)
            Call("int.greater-than", span "failing-string-validator.agent" 6)
        ]
    let failingContext =
        contextWithScalarDefinitions [ failingValidator ] [
            scalarDefinition "NonEmptyString" TString (Some "failing-string-validator?"), "NonEmptyString.construct", "NonEmptyString.unwrap"
        ]
        |> fun context -> withScalarConstructorSpan context "NonEmptyString" "NonEmptyString.construct"
    let failingConstructBody = compileBodyWithInputs failingContext "owning-refined-string-validator-runtime-error" [ TString ] [
        Call("NonEmptyString.construct", span "owning-refined-string-validator-runtime-error.agent" 1)
    ]
    let inactiveBody = compileBodyWithInputs failingContext "owning-refined-string-inactive-cases" [ TOption(TNamed "NonEmptyString"); TResult(TInt, TNamed "NonEmptyString") ] []
    for optimization in [ LlvmOptimization.O0; LlvmOptimization.O2 ] do
        use failingConstruct = compileOwningNative toolchain "owning-refined-string-validator-runtime-error" optimization failingConstructBody
        let runtimeFailure = owningError (fun () -> failingConstruct.Execute([ StringValue "ok" ], 4096, 16) |> ignore)
        check ($"{optimization} String validator runtime failures keep their original runtime classification") (
            runtimeFailure.Code = "RUNTIME_DIVIDE_BY_ZERO" && runtimeFailure.Word = Some "divide")
        use inactive = compileOwningNative toolchain "owning-refined-string-inactive-cases" optimization inactiveBody
        let inactiveValues = [
            OptionValue(TNamed "NonEmptyString", None)
            ResultValue(TInt, TNamed "NonEmptyString", Ok(IntValue 9L))
        ]
        check ($"{optimization} None and Result alternatives without refined String do not invoke its validator") (
            inactive.Execute(inactiveValues, 4096, 32).Values = inactiveValues)

    let floatValidator = wordEntry "accept-float?" [ TFloat ] [ TBool ] Set.empty [
        Call("drop", span "owning-string-float-validator.agent" 1)
        Push(LBool true, span "owning-string-float-validator.agent" 2)
    ]
    for scalarName, baseType, validatorName in [ "FloatTag", TFloat, "accept-float?" ] do
        let context =
            contextWithScalarDefinitions [ floatValidator ] [
                scalarDefinition scalarName baseType (Some validatorName), scalarName + ".construct", scalarName + ".unwrap"
            ]
        let body = compileBodyWithInputs context ("owning-unsupported-string-related-" + scalarName) [ TNamed scalarName ] []
        let diagnostic = errorOf (fun () -> compileOwningNative toolchain ("owning-unsupported-string-related-" + scalarName) LlvmOptimization.O0 body |> ignore)
        check ($"{scalarName} refinements remain unsupported beside the refined String slice") (
            diagnostic.Code = "IR_OWNING_STACK_TYPE_UNSUPPORTED")

    let mailboxContext =
        contextWithRecordDefinitions [ nonEmptyValidator ] [
            recordDefinition "MailboxState" [ recordField "owner" (TNamed "NonEmptyString") ]
            recordDefinition "MailboxContinuation" [ recordField "marker" TInt ]
        ] [
            scalarDefinition "NonEmptyString" TString (Some "is-non-empty?"), "NonEmptyString.construct", "NonEmptyString.unwrap"
        ]
    let mailboxProgram = Compiler.compileIrProgram mailboxContext
    let initialize = Compiler.compileIrBodyAgainstProgram mailboxContext mailboxProgram "owning-refined-string-mailbox-initialize" [ TString ] [
        Call("drop", span "owning-refined-string-mailbox.agent" 1)
        Push(LString "owner", span "owning-refined-string-mailbox.agent" 2)
        Call("NonEmptyString.construct", span "owning-refined-string-mailbox.agent" 3)
        Call("mailboxState.new", span "owning-refined-string-mailbox.agent" 4)
    ]
    let beginTurn = Compiler.compileIrBodyAgainstProgram mailboxContext mailboxProgram "owning-refined-string-mailbox-begin" [ TNamed "MailboxState"; TString ] [
        Call("drop", span "owning-refined-string-mailbox.agent" 5)
        Push(LInt 1L, span "owning-refined-string-mailbox.agent" 6)
        Call("mailboxContinuation.new", span "owning-refined-string-mailbox.agent" 7)
    ]
    let resume = Compiler.compileIrBodyAgainstProgram mailboxContext mailboxProgram "owning-refined-string-mailbox-resume" [ TNamed "MailboxState"; TNamed "MailboxContinuation"; TString ] [
        Call("drop", span "owning-refined-string-mailbox.agent" 8)
        Call("drop", span "owning-refined-string-mailbox.agent" 9)
    ]
    let mailbox =
        OwningStackAot.compileMailboxWithProfile
            toolchain
            LlvmOptimization.O0
            OwningRuntimeProfile.Diagnostic
            (Path.Combine(artifactRoot, "owning-refined-mailbox-string"))
            initialize
            beginTurn
            resume
    check "mailbox layout admits a nested validated String scalar" (
        mailbox.Entries.Length = 3
        && (mailbox.Layouts |> List.exists (fun layout -> layout.TypeName = "NonEmptyString")))
    check "mailbox begin validates refined String inputs before the entry frame" (
        mailbox.LlvmIr.Contains("mailbox.validation.frame.entered", StringComparison.Ordinal)
        && mailbox.LlvmIr.Contains("@agentlang_mailbox_begin_frame", StringComparison.Ordinal))
    check "mailbox callback metadata bound includes the refined admission allocas" (
        (mailbox.CallbackMetadataPerEntryBytes.TryFind "begin" |> Option.exists (fun bytes -> bytes > 0))
        && mailbox.BackendMetadataPeakBoundBytes >= int64 (mailbox.CallbackMetadataPerEntryBytes["begin"]))
    let mailboxBeginOutputTypeIndexes: uint32 list = mailbox.Entries[1].OutputTypeIndexes
    let validMailboxTurn = invokeRawMailboxEntry mailbox 1 [ mailboxStringBytes "owner"; mailboxStringBytes "turn" ]
    check "valid refined String mailbox input executes and publishes both begin outputs" (
        validMailboxTurn.Status = 0
        && List.map (fun (output: RawMailboxOutputSlice) -> output.TypeIndex) validMailboxTurn.Outputs = mailboxBeginOutputTypeIndexes
        && validMailboxTurn.Context.CallDepth = 0u)
    // The external MailboxState owner String is 24 bytes in UTF-16 form and
    // the turn String is 16 bytes. The verified body has one 8-byte marker
    // field copy in MailboxContinuation construction; admission adds no payload
    // copy or move.
    let expectedStringMailboxInputCopyBytes = uint64 ((mailboxStringBytes "owner").Length + (mailboxStringBytes "turn").Length)
    let expectedStringMailboxBodyDeepCopyBytes = 8UL
    check ($"refined String mailbox admission preserves verified body counters (deep={validMailboxTurn.Context.DeepCopyBytes}, expected-deep={expectedStringMailboxBodyDeepCopyBytes}, move={validMailboxTurn.Context.MoveBytes}, input={validMailboxTurn.Context.InputCopyBytes}, expected-input={expectedStringMailboxInputCopyBytes})") (
        validMailboxTurn.Context.DeepCopyBytes = expectedStringMailboxBodyDeepCopyBytes
        && validMailboxTurn.Context.MoveBytes = 0UL
        && validMailboxTurn.Context.InputCopyBytes = expectedStringMailboxInputCopyBytes)
    let invalidMailboxTurn = invokeRawMailboxEntry mailbox 1 [ mailboxStringBytes ""; mailboxStringBytes "turn" ]
    check "invalid nested refined String input fails before begin publishes output" (
        invalidMailboxTurn.Status <> 0
        && (invalidMailboxTurn.Diagnostic |> Option.exists (fun diagnostic -> diagnostic.EntryRole = Some "begin" && diagnostic.Code = "REFINEMENT_FAILED"))
        && (invalidMailboxTurn.Outputs |> List.forall (fun output -> output.TypeIndex = UInt32.MaxValue && output.OffsetBytes = 0u && output.OwnerEndBytes = 0u)))

let private testOwningNominalStringSlice () =
    let toolchain = LlvmToolchain.discover()
    let textTag = scalarDefinition "TextTag" TString None, "TextTag.make", "TextTag.value"
    let otherTag = scalarDefinition "OtherTag" TString None, "OtherTag.make", "OtherTag.value"
    let context = contextWithScalarDefinitions [] [ textTag; otherTag ]
    let noScalarValidators (body: VerifiedIrBody) =
        (VerifiedIrProgram.inspect (VerifiedIrBody.program body)).NominalTypesByKey
        |> Map.forall (fun _ definition ->
            match definition with
            | IrScalarDefinition scalar -> Option.isNone scalar.ValidatorCall
            | _ -> true)
    let bytes (hex: string) = Convert.FromHexString hex
    let nulText = String([| 'a'; char 0; 'b' |])
    let isolatedSurrogate = String([| char 0xD800 |])
    let nulBytes = bytes "03000000000000006100000062000000"
    let surrogateBytes = bytes "010000000000000000d8000000000000"
    let sameBytes = bytes "0400000000000000730061006d006500"
    let tailBytes = bytes "04000000000000007400610069006c00"
    let textTagString = NamedValue("TextTag", StringValue "same")
    let otherTagString = NamedValue("OtherTag", StringValue "same")
    let identityBody = compileBodyWithInputs context "owning-nominal-string-identity" [ TNamed "TextTag"; TNamed "OtherTag" ] []
    check "unvalidated String wrappers have no frozen validator calls" (noScalarValidators identityBody)

    for optimization in [ LlvmOptimization.O0; LlvmOptimization.O2 ] do
        use identity = compileOwningNative toolchain "owning-nominal-string-identity" optimization identityBody
        let textLayout = identity.Layouts |> List.find (fun layout -> layout.TypeName = "TextTag")
        let otherLayout = identity.Layouts |> List.find (fun layout -> layout.TypeName = "OtherTag")
        check ($"{optimization} equal String payload wrappers retain distinct nominal types") (
            textLayout.Type <> otherLayout.Type
            && textLayout.IsDynamic && otherLayout.IsDynamic
            && textLayout.MinimumPayloadBytes = 8 && otherLayout.MinimumPayloadBytes = 8
            && textLayout.MinimumExtentBytes = 8 && otherLayout.MinimumExtentBytes = 8)
        let stringDescriptor typeId =
            identity.LlvmIr.Contains(
                $"i32 5, i32 {typeId}, i32 0, i32 0, i32 4294967295, i32 4294967295, i32 8, i32 8, i32 0",
                StringComparison.Ordinal)
        check ($"{optimization} equal String payload wrappers have separate descriptor TypeIds") (
            stringDescriptor 4 && stringDescriptor 5 && stringDescriptor 6)
        let identityResult = identity.Execute([ textTagString; otherTagString ], 4096, 32)
        check ($"{optimization} exact host names roundtrip equal nominal String payloads") (
            identityResult.Values = [ textTagString; otherTagString ]
            && Convert.ToHexString(identityResult.RetainedOutputBytes).ToLowerInvariant() =
               Convert.ToHexString(Array.append sameBytes sameBytes).ToLowerInvariant())

        for label, invalidValues in [
            "bare String", [ StringValue "same"; otherTagString ]
            "wrong first wrapper", [ otherTagString; otherTagString ]
            "wrong second wrapper", [ textTagString; textTagString ]
        ] do
            let retained = Array.create 32 0xA5uy
            let mutable rejected = false
            try
                identity.ExecuteInto(invalidValues, 4096, retained) |> ignore
            with :? ArgumentException -> rejected <- true
            check ($"{optimization} host rejects {label} for an unvalidated String nominal") rejected
            check ($"{optimization} {label} rejection leaves retained output unchanged") (retained = Array.create 32 0xA5uy)

        let emptyBody = compileBodyWithInputs context "owning-nominal-string-empty-input" [ TNamed "TextTag" ] []
        use emptyInput = compileOwningNative toolchain "owning-nominal-string-empty-input" optimization emptyBody
        let emptyValue = NamedValue("TextTag", StringValue "")
        let emptyResult = emptyInput.Execute([ emptyValue ], 4096, 8)
        check ($"{optimization} unvalidated TextTag accepts and roundtrips an empty String") (
            emptyResult.Values = [ emptyValue ]
            && Convert.ToHexString(emptyResult.RetainedOutputBytes).ToLowerInvariant() = "0000000000000000")

        let wrapUnwrapBody = compileBodyWithInputs context "owning-nominal-string-wrap-unwrap" [ TString ] [
            Call("TextTag.make", span "owning-nominal-string-wrap-unwrap.agent" 1)
            Call("TextTag.value", span "owning-nominal-string-wrap-unwrap.agent" 2)
        ]
        use wrapUnwrap = compileOwningNative toolchain "owning-nominal-string-wrap-unwrap" optimization wrapUnwrapBody
        check ($"{optimization} unvalidated String wrapping emits no validator call") (
            noScalarValidators wrapUnwrapBody
            && not (wrapUnwrap.LlvmIr.Contains("validator.arguments", StringComparison.Ordinal))
            && not (wrapUnwrap.LlvmIr.Contains("REFINEMENT_FAILED", StringComparison.Ordinal)))
        for label, value, expectedBytes in [
            "embedded NUL", nulText, nulBytes
            "isolated high surrogate", isolatedSurrogate, surrogateBytes
        ] do
            let result = wrapUnwrap.Execute([ StringValue value ], 4096, expectedBytes.Length)
            check ($"{optimization} wrap/unwrap preserves {label} UTF-16 bytes without copying or moving") (
                result.Values = [ StringValue value ]
                && result.RetainedBytesWritten = expectedBytes.Length
                && result.RetainedOutputBytes = expectedBytes
                && result.Metrics.DeepCopyBytes = 0UL
                && result.Metrics.MoveBytes = 0UL)

    let envelope = recordDefinition "StringOwnerEnvelope" [
        recordField "owner" (TNamed "TextTag")
        recordField "tail" TString
    ]
    let nestedContext = contextWithRecordDefinitions [] [ envelope ] [ textTag; otherTag ]
    let nestedTypes = [
        TNamed "StringOwnerEnvelope"
        TOption(TNamed "TextTag")
        TOption(TNamed "TextTag")
        TResult(TNamed "TextTag", TInt)
        TResult(TNamed "TextTag", TInt)
        TResult(TInt, TNamed "OtherTag")
    ]
    let nestedBody = compileBodyWithInputs nestedContext "owning-nominal-string-nested" nestedTypes []
    let nominalText text = NamedValue("TextTag", StringValue text)
    let nestedValues = [
        RecordValue("StringOwnerEnvelope", Map.ofList [ "owner", nominalText nulText; "tail", StringValue "tail" ])
        OptionValue(TNamed "TextTag", Some(nominalText isolatedSurrogate))
        OptionValue(TNamed "TextTag", None)
        ResultValue(TNamed "TextTag", TInt, Ok(nominalText "same"))
        ResultValue(TNamed "TextTag", TInt, Error(IntValue 42L))
        ResultValue(TInt, TNamed "OtherTag", Error(otherTagString))
    ]
    let zeroTag = bytes "0000000000000000"
    let oneTag = bytes "0100000000000000"
    let int42 = bytes "2a00000000000000"
    let expectedNestedBytes =
        [ nulBytes; tailBytes
          zeroTag; surrogateBytes
          oneTag
          zeroTag; sameBytes
          oneTag; int42
          oneTag; sameBytes ]
        |> Array.concat
    let unwrapFieldBody = compileBodyWithInputs nestedContext "owning-nominal-string-owner-end" [ TNamed "StringOwnerEnvelope" ] [
        Call("stringOwnerEnvelope.owner", span "owning-nominal-string-owner-end.agent" 1)
        Call("TextTag.value", span "owning-nominal-string-owner-end.agent" 2)
    ]
    check "nested unvalidated String program remains free of frozen predicates" (
        noScalarValidators nestedBody && noScalarValidators unwrapFieldBody)
    for optimization in [ LlvmOptimization.O0; LlvmOptimization.O2 ] do
        use nested = compileOwningNative toolchain "owning-nominal-string-nested" optimization nestedBody
        let roundtrip = nested.Execute(nestedValues, 8192, 256)
        check ($"{optimization} unvalidated String nominals survive nested records and active Option/Result cases") (
            roundtrip.Values = nestedValues
            && roundtrip.Metrics.InputBytes = 128
            && Convert.ToHexString(roundtrip.RetainedOutputBytes).ToLowerInvariant() =
               Convert.ToHexString(expectedNestedBytes).ToLowerInvariant())
        use unwrapField = compileOwningNative toolchain "owning-nominal-string-owner-end" optimization unwrapFieldBody
        let ownerEndResult =
            unwrapField.Execute(
                [ RecordValue("StringOwnerEnvelope", Map.ofList [ "owner", nominalText "ok"; "tail", StringValue "tail" ]) ],
                4096,
                16)
        let preservesOwnerRange =
            ownerEndResult.LayoutEvents
            |> List.exists (fun event ->
                event.Kind = "descriptor-transfer"
                && event.TypeId = 7u
                && event.OffsetBytes = 0
                && event.SourceOffsetBytes = Some 32
                && event.SourceExtentBytes = Some 16)
        check ($"{optimization} unwrapped String result escapes without losing its multi-field owner range") (
            ownerEndResult.Values = [ StringValue "ok" ]
            && Convert.ToHexString(ownerEndResult.RetainedOutputBytes).ToLowerInvariant() = "02000000000000006f006b0000000000"
            && ownerEndResult.Metrics.InputBytes = 32
            && ownerEndResult.Metrics.HostEncodedInputBytes = 32
            && ownerEndResult.Metrics.DeepCopyBytes = 0UL
            && ownerEndResult.Metrics.MoveBytes = 0UL
            && preservesOwnerRange)

    let mailboxContext =
        contextWithRecordDefinitions [] [
            recordDefinition "MailboxState" [ recordField "owner" (TNamed "TextTag") ]
            recordDefinition "MailboxContinuation" [ recordField "marker" TInt ]
        ] [ textTag ]
    let mailboxProgram = Compiler.compileIrProgram mailboxContext
    let initialize = Compiler.compileIrBodyAgainstProgram mailboxContext mailboxProgram "owning-nominal-string-mailbox-initialize" [ TString ] [
        Call("TextTag.make", span "owning-nominal-string-mailbox.agent" 1)
        Call("mailboxState.new", span "owning-nominal-string-mailbox.agent" 2)
    ]
    let beginTurn = Compiler.compileIrBodyAgainstProgram mailboxContext mailboxProgram "owning-nominal-string-mailbox-begin" [ TNamed "MailboxState"; TString ] [
        Call("drop", span "owning-nominal-string-mailbox.agent" 3)
        Call("dup", span "owning-nominal-string-mailbox.agent" 4)
        Call("mailboxState.owner", span "owning-nominal-string-mailbox.agent" 5)
        Call("TextTag.value", span "owning-nominal-string-mailbox.agent" 6)
        Call("string.length", span "owning-nominal-string-mailbox.agent" 7)
        Call("drop", span "owning-nominal-string-mailbox.agent" 8)
        Push(LInt 1L, span "owning-nominal-string-mailbox.agent" 9)
        Call("mailboxContinuation.new", span "owning-nominal-string-mailbox.agent" 10)
    ]
    let resume = Compiler.compileIrBodyAgainstProgram mailboxContext mailboxProgram "owning-nominal-string-mailbox-resume" [ TNamed "MailboxState"; TNamed "MailboxContinuation"; TString ] [
        Call("drop", span "owning-nominal-string-mailbox.agent" 17)
        Call("drop", span "owning-nominal-string-mailbox.agent" 18)
    ]
    check "unvalidated String mailbox program has no frozen validator" (
        noScalarValidators initialize && noScalarValidators beginTurn && noScalarValidators resume)
    for optimization in [ LlvmOptimization.O0; LlvmOptimization.O2 ] do
        let mailbox =
            OwningStackAot.compileMailboxWithProfile
                toolchain
                optimization
                OwningRuntimeProfile.Diagnostic
                (Path.Combine(artifactRoot, "owning-nominal-string-mailbox", string optimization))
                initialize
                beginTurn
                resume
        let textLayout = mailbox.Layouts |> List.find (fun layout -> layout.TypeName = "TextTag")
        check ($"{optimization} unvalidated String wrapper is admitted in mailbox layout") (
            textLayout.IsDynamic
            && textLayout.MinimumPayloadBytes = 8
            && textLayout.MinimumExtentBytes = 8
            && not (mailbox.LlvmIr.Contains("mailbox.validation.frame.entered", StringComparison.Ordinal)))
        let outputTypeIndexes: uint32 list = mailbox.Entries[1].OutputTypeIndexes
        let turnBytes = mailboxStringBytes "turn"
        let validTurn = invokeRawMailboxEntry mailbox 1 [ nulBytes; turnBytes ]
        // The callback imports a 16-byte dynamic MailboxState owner and a
        // 16-byte turn. `dup` of the imported state deep-copies its full 16-byte
        // owner range; the field accessor, nominal unwrap and String length
        // read are zero-copy. MailboxContinuation construction copies its
        // 8-byte marker field.
        let expectedMailboxInputCopyBytes = uint64 (nulBytes.Length + turnBytes.Length)
        let expectedMailboxBodyDeepCopyBytes = uint64 (nulBytes.Length + 8)
        check ($"{optimization} direct mailbox callback imports and roundtrips a nominal String owner (status={validTurn.Status}, output-types={validTurn.Outputs |> List.map (fun output -> output.TypeIndex)}, expected-types={outputTypeIndexes}, depth={validTurn.Context.CallDepth}, input={validTurn.Context.InputCopyBytes}, deep={validTurn.Context.DeepCopyBytes}, move={validTurn.Context.MoveBytes})") (
            validTurn.Status = 0
            && List.map (fun (output: RawMailboxOutputSlice) -> output.TypeIndex) validTurn.Outputs = outputTypeIndexes
            && validTurn.Context.CallDepth = 0u
            && validTurn.Context.InputCopyBytes = expectedMailboxInputCopyBytes
            && validTurn.Context.DeepCopyBytes = expectedMailboxBodyDeepCopyBytes
            && validTurn.Context.MoveBytes = 0UL)
        let stateOutput = validTurn.Outputs.Head
        let stateOffset = int stateOutput.OffsetBytes
        let stateOwnerBytes =
            if stateOffset >= 0 && stateOffset <= validTurn.StackBytes.Length - nulBytes.Length then
                validTurn.StackBytes[stateOffset .. stateOffset + nulBytes.Length - 1]
            else
                Array.empty
        check ($"{optimization} mailbox output retains the independently pinned UTF-16 owner bytes") (
            stateOutput.OwnerEndBytes >= stateOutput.OffsetBytes + uint32 nulBytes.Length
            && stateOwnerBytes = nulBytes)

        let failedTurn = invokeRawMailboxEntry mailbox 1 [ bytes "0000000000000001"; turnBytes ]
        check ($"{optimization} malformed nominal String import after preflight invalidates callback outputs") (
            failedTurn.Status <> 0
            && (failedTurn.Diagnostic |> Option.exists (fun diagnostic -> diagnostic.EntryRole = Some "begin"))
            && (failedTurn.Outputs |> List.forall (fun output -> output.TypeIndex = UInt32.MaxValue && output.OffsetBytes = 0u && output.OwnerEndBytes = 0u)))

let private testOwningRefinedMailboxVariants () =
    let toolchain = LlvmToolchain.discover()
    let positiveValidator =
        wordEntry "is-positive?" [ TInt ] [ TBool ] Set.empty [
            Push(LInt 0L, span "owning-mailbox-positive-validator.agent" 1)
            Call("int.greater-than", span "owning-mailbox-positive-validator.agent" 2)
        ]
    let scalarDefinitions = [
        scalarDefinition "PositiveId" TInt (Some "is-positive?"), "PositiveId.construct", "PositiveId.unwrap"
    ]
    let createMailbox label (ownerType: LangType) (initializeExpressions: Expr list) =
        let context =
            contextWithRecordDefinitions [ positiveValidator ] [
                recordDefinition "MailboxState" [ recordField "owner" ownerType ]
                recordDefinition "MailboxContinuation" [ recordField "marker" TInt ]
            ] scalarDefinitions
        let program = Compiler.compileIrProgram context
        let initialize = Compiler.compileIrBodyAgainstProgram context program (label + "-initialize") [ TString ] initializeExpressions
        let beginTurn = Compiler.compileIrBodyAgainstProgram context program (label + "-begin") [ TNamed "MailboxState"; TString ] [
            Call("drop", span (label + ".agent") 10)
            Push(LInt 1L, span (label + ".agent") 11)
            Call("mailboxContinuation.new", span (label + ".agent") 12)
        ]
        let resume = Compiler.compileIrBodyAgainstProgram context program (label + "-resume") [ TNamed "MailboxState"; TNamed "MailboxContinuation"; TString ] [
            Call("drop", span (label + ".agent") 13)
            Call("drop", span (label + ".agent") 14)
        ]
        let mailbox =
            OwningStackAot.compileMailboxWithProfile
                toolchain
                LlvmOptimization.O0
                OwningRuntimeProfile.Diagnostic
                (Path.Combine(artifactRoot, label))
                initialize
                beginTurn
                resume
        context, program, mailbox

    let optionField = TOption(TNamed "PositiveId")
    let optionInitialize = [
        Call("drop", span "owning-mailbox-option.agent" 1)
        ConstructContainer(OptionNone, [ TNamed "PositiveId" ], span "owning-mailbox-option.agent" 2)
        Call("mailboxState.new", span "owning-mailbox-option.agent" 3)
    ]
    let optionContext, optionProgram, optionMailbox = createMailbox "owning-refined-mailbox-option" optionField optionInitialize
    let noneBody = Compiler.compileIrBodyAgainstProgram optionContext optionProgram "owning-mailbox-option-none-value" [] [
        ConstructContainer(OptionNone, [ TNamed "PositiveId" ], span "owning-mailbox-option-none.agent" 1)
    ]
    use noneNative = compileOwningNative toolchain "owning-mailbox-option-none-value" LlvmOptimization.O0 noneBody
    let inactiveOptionBytes = noneNative.Execute([], 4096, 32).RetainedOutputBytes
    let inactiveOption = invokeRawMailboxEntry optionMailbox 1 [ inactiveOptionBytes; mailboxStringBytes "turn" ]
    check "inactive Option payload skips its refined predicate in the mailbox callback" (
        inactiveOption.Status = 0
        && inactiveOption.Outputs.Length = 2
        && inactiveOption.Context.CallDepth = 0u)
    let activeOptionBytes = Array.concat [ BitConverter.GetBytes(0u); BitConverter.GetBytes(0u); mailboxIntBytes 0L ]
    let activeOption = invokeRawMailboxEntry optionMailbox 1 [ activeOptionBytes; mailboxStringBytes "turn" ]
    check "active Option payload runs its refined predicate in the mailbox callback" (
        activeOption.Status <> 0
        && (activeOption.Diagnostic |> Option.exists (fun diagnostic -> diagnostic.Code = "REFINEMENT_FAILED" && diagnostic.EntryRole = Some "begin")))

    let resultField = TResult(TNamed "PositiveId", TString)
    let resultErrorExpression = [
        Call("drop", span "owning-mailbox-result.agent" 1)
        Push(LString "inactive", span "owning-mailbox-result.agent" 2)
        ConstructContainer(ResultError, [ TNamed "PositiveId"; TString ], span "owning-mailbox-result.agent" 3)
        Call("mailboxState.new", span "owning-mailbox-result.agent" 4)
    ]
    let resultContext, resultProgram, resultMailbox = createMailbox "owning-refined-mailbox-result" resultField resultErrorExpression
    let resultErrorBody = Compiler.compileIrBodyAgainstProgram resultContext resultProgram "owning-mailbox-result-error-value" [] [
        Push(LString "inactive", span "owning-mailbox-result-error.agent" 1)
        ConstructContainer(ResultError, [ TNamed "PositiveId"; TString ], span "owning-mailbox-result-error.agent" 2)
    ]
    use resultErrorNative = compileOwningNative toolchain "owning-mailbox-result-error-value" LlvmOptimization.O0 resultErrorBody
    let inactiveResultBytes = resultErrorNative.Execute([], 4096, 64).RetainedOutputBytes
    let inactiveResult = invokeRawMailboxEntry resultMailbox 1 [ inactiveResultBytes; mailboxStringBytes "turn" ]
    check "inactive Result payload skips its refined predicate in the mailbox callback" (
        inactiveResult.Status = 0
        && inactiveResult.Outputs.Length = 2
        && inactiveResult.Context.CallDepth = 0u)
    let activeResultBytes = Array.concat [ BitConverter.GetBytes(0u); BitConverter.GetBytes(0u); mailboxIntBytes 0L ]
    let activeResult = invokeRawMailboxEntry resultMailbox 1 [ activeResultBytes; mailboxStringBytes "turn" ]
    check "active Result payload runs its refined predicate in the mailbox callback" (
        activeResult.Status <> 0
        && (activeResult.Diagnostic |> Option.exists (fun diagnostic -> diagnostic.Code = "REFINEMENT_FAILED" && diagnostic.EntryRole = Some "begin")))

    let failingValidator =
        wordEntry "mailbox-failing-validator?" [ TInt ] [ TBool ] Set.empty [
            Call("drop", span "owning-mailbox-failing-validator.agent" 1)
            Push(LInt 1L, span "owning-mailbox-failing-validator.agent" 2)
            Push(LInt 0L, span "owning-mailbox-failing-validator.agent" 3)
            Call("divide", span "owning-mailbox-failing-validator.agent" 4)
            Push(LInt 0L, span "owning-mailbox-failing-validator.agent" 5)
            Call("int.greater-than", span "owning-mailbox-failing-validator.agent" 6)
        ]
    let failingContext =
        contextWithRecordDefinitions [ failingValidator ] [
            recordDefinition "MailboxState" [ recordField "owner" (TNamed "PositiveId") ]
            recordDefinition "MailboxContinuation" [ recordField "marker" TInt ]
        ] [ scalarDefinition "PositiveId" TInt (Some "mailbox-failing-validator?"), "PositiveId.construct", "PositiveId.unwrap" ]
    let failingProgram = Compiler.compileIrProgram failingContext
    let failingInitialize = Compiler.compileIrBodyAgainstProgram failingContext failingProgram "owning-mailbox-validator-error-initialize" [ TString ] [
        Call("drop", span "owning-mailbox-validator-error.agent" 7)
        Push(LInt 1L, span "owning-mailbox-validator-error.agent" 8)
        Call("PositiveId.construct", span "owning-mailbox-validator-error.agent" 9)
        Call("mailboxState.new", span "owning-mailbox-validator-error.agent" 10)
    ]
    let failingBegin = Compiler.compileIrBodyAgainstProgram failingContext failingProgram "owning-mailbox-validator-error-begin" [ TNamed "MailboxState"; TString ] [
        Call("drop", span "owning-mailbox-validator-error.agent" 11)
        Push(LInt 1L, span "owning-mailbox-validator-error.agent" 12)
        Call("mailboxContinuation.new", span "owning-mailbox-validator-error.agent" 13)
    ]
    let failingResume = Compiler.compileIrBodyAgainstProgram failingContext failingProgram "owning-mailbox-validator-error-resume" [ TNamed "MailboxState"; TNamed "MailboxContinuation"; TString ] [
        Call("drop", span "owning-mailbox-validator-error.agent" 14)
        Call("drop", span "owning-mailbox-validator-error.agent" 15)
    ]
    let failingMailbox =
        OwningStackAot.compileMailboxWithProfile
            toolchain
            LlvmOptimization.O0
            OwningRuntimeProfile.Diagnostic
            (Path.Combine(artifactRoot, "owning-refined-mailbox-validator-runtime-error"))
            failingInitialize
            failingBegin
            failingResume
    let failingAdmission = invokeRawMailboxEntry failingMailbox 1 [ mailboxIntBytes 1L; mailboxStringBytes "turn" ]
    check "mailbox predicate runtime errors retain their code and balance validation cleanup" (
        failingAdmission.Status <> 0
        && (failingAdmission.Diagnostic |> Option.exists (fun diagnostic -> diagnostic.Code = "RUNTIME_DIVIDE_BY_ZERO"))
        && failingAdmission.Context.CallDepth = 0u
        && failingAdmission.Context.ActiveLocalReservedBytes = 0u
        && (failingAdmission.Outputs |> List.forall (fun output -> output.TypeIndex = UInt32.MaxValue && output.OffsetBytes = 0u && output.OwnerEndBytes = 0u)))

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

let private testRecordValidatorConformance () =
    let boundsValidator =
        wordEntry "range.bounds-valid?" [ TNamed "Range" ] [ TBool ] Set.empty [
            Call("dup", span "range-bounds-valid.agent" 1)
            Call("range.minimum", span "range-bounds-valid.agent" 2)
            Call("swap", span "range-bounds-valid.agent" 3)
            Call("range.maximum", span "range-bounds-valid.agent" 4)
            Call("int.less-or-equal", span "range-bounds-valid.agent" 5)
        ]
    let rangeValidator =
        wordEntry "range.valid?" [ TNamed "Range" ] [ TBool ] Set.empty [
            Call("range.bounds-valid?", span "range-valid.agent" 1)
        ]
    let rangeMaker =
        wordEntry "range.make" [ TInt; TInt ] [ TNamed "Range" ] Set.empty [
            Call("range.new", span "range-maker.agent" 4)
        ]
    let rangeCopy =
        wordEntry "range.copy" [ TNamed "Range" ] [ TNamed "Range" ] Set.empty [
            Call("dup", span "range-copy.agent" 1)
            Call("range.minimum", span "range-copy.agent" 2)
            Call("swap", span "range-copy.agent" 3)
            Call("range.maximum", span "range-copy.agent" 4)
            Call("range.new", span "range-copy.agent" 5)
        ]
    let rangeBase = recordDefinition "Range" [ recordField "minimum" TInt; recordField "maximum" TInt ]
    let rangeDefinition = { rangeBase with Validator = Some "range.valid?" }
    let context = contextWithRecordDefinitions [ boundsValidator; rangeValidator; rangeMaker; rangeCopy ] [ rangeDefinition ] []
    let program = Compiler.compileIrProgram context
    let validBody =
        Compiler.compileIrBodyAgainstProgram context program "range-valid" [] [
            Push(LInt 2L, span "range-entry.agent" 1)
            Push(LInt 8L, span "range-entry.agent" 2)
            Call("range.make", span "range-entry.agent" 3)
        ]
    let invalidBody =
        Compiler.compileIrBodyAgainstProgram context program "range-invalid" [] [
            Push(LInt 8L, span "range-entry.agent" 10)
            Push(LInt 2L, span "range-entry.agent" 11)
            Call("range.make", span "range-entry.agent" 12)
        ]
    let expected = RecordValue("Range", Map.ofList [ "minimum", IntValue 2L; "maximum", IntValue 8L ])
    let interpreted, expectedSteps = interpreterResultAndSteps "range-valid" validBody
    check "record validator accepts a valid complete record" (interpreted = [ expected ])
    for optimization in [ LlvmOptimization.O0; LlvmOptimization.O2 ] do
        use native = compileNative "record-validator-valid" optimization validBody
        let actual = native.Execute "range-valid"
        check ($"{optimization} record validator valid constructor matches interpreter") (actual.Values = interpreted)
        check ($"{optimization} record validator preserves instruction fuel") (actual.StepsConsumed = expectedSteps)

    let interpretedError = errorOf (fun () -> interpreterResult "range-invalid" invalidBody |> ignore)
    check "record validator rejects an invalid complete record with the constructor site" (
        interpretedError.Code = "RECORD_VALIDATION_FAILED"
        && interpretedError.Word = Some "range.new"
        && interpretedError.Message = "Value does not satisfy Range's validation predicate."
        && interpretedError.Expected = [ "validator returns true" ]
        && interpretedError.Actual = [ "false" ]
        && interpretedError.Span = Some(span "range-maker.agent" 4))
    for optimization in [ LlvmOptimization.O0; LlvmOptimization.O2 ] do
        use native = compileNative "record-validator-invalid" optimization invalidBody
        let actualError = errorOf (fun () -> native.Execute "range-invalid" |> ignore)
        check ($"{optimization} record validator failure matches interpreter diagnostic") (actualError = interpretedError)

    let copyEntryBody =
        Compiler.compileIrBodyAgainstProgram context program "range-copy-entry" [ TNamed "Range" ] [
            Call("range.copy", span "range-copy-entry.agent" 1)
        ]
    check "record constructor and validator dependencies stay bound to one verified program" (
        Object.ReferenceEquals(VerifiedIrBody.program validBody, program)
        && Object.ReferenceEquals(VerifiedIrBody.program copyEntryBody, program))
    for optimization in [ LlvmOptimization.O0; LlvmOptimization.O2 ] do
        use initializer = compileNative "record-validator-retained-init" optimization validBody
        use initial = initializer.ExecuteRetained "range-valid"
        use reentry = compileNative "record-validator-retained-reentry" optimization copyEntryBody
        use copied =
            reentry.ExecuteRetainedWithInputs(
                "range-copy-entry",
                Some initial,
                [ IrEntryArgument.RetainedRoot 0 ])
        check ($"{optimization} record validator rechecks a retained root during reentry") (copied.Decode() = [ expected ])

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

    let listContext = contextWith []
    let listProgram = Compiler.compileIrProgram listContext
    let listTailSpan = span "unsupported-list-tail.agent" 1
    let listTailBody =
        Compiler.compileIrBodyAgainstProgram listContext listProgram "unsupported-list-tail" [ TList TInt ]
            [ Call("list.tail", listTailSpan) ]
    let listTailError = errorOf (fun () -> LlvmAot.emit listTailBody |> ignore)
    check "LLVM keeps list.tail outside its supported native type slice with an explicit diagnostic" (
        listTailError.Code = "IR_LLVM_UNSUPPORTED_TYPE"
        && (listTailError.Actual |> List.exists (fun actual -> actual.Contains("List<Int>", StringComparison.Ordinal))))

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

let private testNativeModuleValidationBeforeTools () =
    let context = contextWith []
    let program = Compiler.compileIrProgram context
    let body = Compiler.compileIrBodyAgainstProgram context program "module-entry" [] [ Push(LInt 1L, span "module-validation.agent" 1) ]
    let entry name body = { Name = name; Body = body }
    let unavailableToolchain = LlvmToolchain("missing-clang-for-validation-test.exe", "missing-lld-for-validation-test.exe", None)
    let outputDirectory = Path.Combine(artifactRoot, "invalid-module")
    let rejectsArgument entries =
        try
            LlvmAot.compileModule unavailableToolchain LlvmOptimization.O0 outputDirectory NativeDiagnosticSources.empty entries |> ignore
            false
        with
        | :? ArgumentException -> true
        | _ -> false

    check "empty module entry name is rejected before tool invocation" (rejectsArgument [ entry "" body ])
    check "duplicate module entry names are rejected before tool invocation" (rejectsArgument [ entry "same" body; entry "same" body ])

    let otherContext = contextWith []
    let otherProgram = Compiler.compileIrProgram otherContext
    let otherBody = Compiler.compileIrBodyAgainstProgram otherContext otherProgram "other-entry" [] [ Push(LInt 2L, span "module-validation.agent" 2) ]
    check "cross-program module entries are rejected before tool invocation" (
        rejectsArgument [ entry "first" body; entry "second" otherBody ])

    let unsupportedContext = contextWith []
    let unsupportedProgram = Compiler.compileIrProgram unsupportedContext
    let unsupportedSpan = span "module-unsupported-type.agent" 3
    let unsupportedBody =
        Compiler.compileIrBodyAgainstProgram unsupportedContext unsupportedProgram "unsupported-type" [] [
            Push(LInt 7L, unsupportedSpan)
            Push(LInt 1L, unsupportedSpan)
            Push(LInt 2L, unsupportedSpan)
            Call("int.scale-ratio-toward-zero", unsupportedSpan)
            Call("drop", unsupportedSpan)
        ]
    let unsupportedTypeRejected =
        try
            LlvmAot.compileModule unavailableToolchain LlvmOptimization.O0 outputDirectory NativeDiagnosticSources.empty [ entry "unsupported-type" unsupportedBody ] |> ignore
            false
        with
        | LanguageException diagnostic -> diagnostic.Code = "IR_LLVM_UNSUPPORTED_TYPE"
        | _ -> false
    check "unsupported module types are rejected before tool invocation" unsupportedTypeRejected

    let effectSource = span "module-effect.agent" 4
    let effectWord = wordEntry "module-write" [ TString ] [ TUnit ] (Set.singleton "console.write") [ Call("console.write", effectSource) ]
    let effectContext = contextWith [ effectWord ]
    let effectProgram = Compiler.compileIrProgram effectContext
    let effectBody =
        Compiler.compileIrBodyAgainstProgram effectContext effectProgram "effectful-entry" [] [
            Push(LString "hello", effectSource)
            Call("module-write", effectSource)
        ]
    let unsupportedEffectRejected =
        try
            LlvmAot.compileModule unavailableToolchain LlvmOptimization.O0 outputDirectory NativeDiagnosticSources.empty [ entry "effectful" effectBody ] |> ignore
            false
        with
        | LanguageException diagnostic -> diagnostic.Code = "IR_LLVM_EFFECT_UNSUPPORTED"
        | _ -> false
    check "effectful module entries are rejected before tool invocation" unsupportedEffectRejected

let private testNativeModuleCompilation () =
    let record = recordDefinition "NativePoint" [ recordField "x" TInt; recordField "visible" TBool ]
    let context = contextWithRecords [] [ record ]
    let program = Compiler.compileIrProgram context
    let beginBody = Compiler.compileIrBodyAgainstProgram context program "begin-body" [] [ Push(LInt 17L, span "module-begin.agent" 1) ]
    let resumeBody = Compiler.compileIrBodyAgainstProgram context program "resume-body" [ TInt ] [
        Push(LInt 1L, span "module-resume.agent" 2)
        Call("add", span "module-resume.agent" 3)
    ]
    let failureBody =
        Compiler.compileIrBodyAgainstProgram context program "failure-body" [] [
            Push(LInt Int64.MaxValue, span "failure-λ.agent" 1)
            Push(LInt 1L, span "failure-λ.agent" 2)
            Call("add", span "failure-λ.agent" 3)
        ]
    let entries = [
        { Name = "C.failure"; Body = failureBody }
        { Name = "B.resume-☃"; Body = resumeBody }
        { Name = "A.begin"; Body = beginBody }
    ]
    let toolchain = LlvmToolchain.discover()
    let build name optimization =
        let directory = Path.Combine(artifactRoot, "module", name, string optimization)
        LlvmAot.compileModule toolchain optimization directory NativeDiagnosticSources.empty entries
    let optimization0 = build "fresh-a" LlvmOptimization.O0
    let optimization2 = build "fresh-b" LlvmOptimization.O2
    check "native module fingerprint is deterministic across output paths and optimization" (
        optimization0.Fingerprint = optimization2.Fingerprint)
    check "native module IR is independent of output path and optimization" (
        List.map File.ReadAllText optimization0.EntryIrPaths = List.map File.ReadAllText optimization2.EntryIrPaths)
    check "native module metadata source is deterministic across output paths and optimization" (
        File.ReadAllText optimization0.MetadataSourcePath = File.ReadAllText optimization2.MetadataSourcePath)

    use fixture = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "module-abi-v1.json")))
    let fixtureRoot = fixture.RootElement
    let diagnosticFixture = fixtureRoot.GetProperty("diagnostic")
    let entryFixture = fixtureRoot.GetProperty("entry")
    let moduleFixture = fixtureRoot.GetProperty("module")
    let offset (structType: Type) (field: string) = Marshal.OffsetOf(structType, field).ToInt32()
    check "independent module fixture fixes diagnostic size and offsets" (
        Marshal.SizeOf<RawModuleDiagnostic>() = diagnosticFixture.GetProperty("size").GetInt32()
        && offset typeof<RawModuleDiagnostic> "Code" = diagnosticFixture.GetProperty("code").GetInt32()
        && offset typeof<RawModuleDiagnostic> "Word" = diagnosticFixture.GetProperty("word").GetInt32()
        && offset typeof<RawModuleDiagnostic> "File" = diagnosticFixture.GetProperty("file").GetInt32())
    check "independent module fixture fixes entry size and offsets" (
        Marshal.SizeOf<RawModuleEntry>() = entryFixture.GetProperty("size").GetInt32()
        && offset typeof<RawModuleEntry> "Name" = entryFixture.GetProperty("name").GetInt32()
        && offset typeof<RawModuleEntry> "Execute" = entryFixture.GetProperty("execute").GetInt32()
        && offset typeof<RawModuleEntry> "InputCount" = entryFixture.GetProperty("inputCount").GetInt32()
        && offset typeof<RawModuleEntry> "OutputCount" = entryFixture.GetProperty("outputCount").GetInt32()
        && offset typeof<RawModuleEntry> "WorkspaceCapacity" = entryFixture.GetProperty("workspaceCapacity").GetInt32()
        && offset typeof<RawModuleEntry> "DiagnosticCount" = entryFixture.GetProperty("diagnosticCount").GetInt32()
        && offset typeof<RawModuleEntry> "InputTypeIds" = entryFixture.GetProperty("inputTypeIds").GetInt32()
        && offset typeof<RawModuleEntry> "OutputTypeIds" = entryFixture.GetProperty("outputTypeIds").GetInt32()
        && offset typeof<RawModuleEntry> "Diagnostics" = entryFixture.GetProperty("diagnostics").GetInt32())
    check "independent module fixture fixes descriptor size and offsets" (
        Marshal.SizeOf<RawModuleDescriptor>() = moduleFixture.GetProperty("size").GetInt32()
        && offset typeof<RawModuleDescriptor> "Fingerprint0" = moduleFixture.GetProperty("fingerprint").GetInt32()
        && offset typeof<RawModuleDescriptor> "Program" = moduleFixture.GetProperty("program").GetInt32()
        && offset typeof<RawModuleDescriptor> "Entries" = moduleFixture.GetProperty("entries").GetInt32()
        && offset typeof<RawModuleDescriptor> "TypeNames" = moduleFixture.GetProperty("typeNames").GetInt32())

    let manifest(path: string) = JsonDocument.Parse(File.ReadAllText path)
    use manifest0 = manifest optimization0.ManifestPath
    use manifest2 = manifest optimization2.ManifestPath
    let module0 = manifest0.RootElement.GetProperty("module")
    let module2 = manifest2.RootElement.GetProperty("module")
    check "module manifest records optimization separately from semantic fingerprint" (
        manifest0.RootElement.GetProperty("fingerprint").GetString() = optimization0.Fingerprint
        && manifest2.RootElement.GetProperty("fingerprint").GetString() = optimization2.Fingerprint
        && manifest0.RootElement.GetProperty("optimization").GetString() = "O0"
        && manifest2.RootElement.GetProperty("optimization").GetString() = "O2"
        && module0.GetRawText() = module2.GetRawText())
    let manifestEntries = module0.GetProperty("entries").EnumerateArray() |> Seq.toArray
    check "module entries are ordinally sorted and have deterministic IDs" (
        (manifestEntries |> Array.map (fun item -> item.GetProperty("name").GetString()))
            = [| "A.begin"; "B.resume-☃"; "C.failure" |]
        && (manifestEntries |> Array.mapi (fun index item -> item.GetProperty("id").GetInt32() = index) |> Array.forall id))
    let typeNames = module0.GetProperty("types").EnumerateArray() |> Seq.map (fun item -> item.GetProperty("name").GetString()) |> Seq.toArray
    check "module manifest retains built-in and nominal type names" (typeNames = [| "Int"; "Bool"; "Unit"; "NativePoint" |])

    let library = NativeLibrary.Load optimization0.LibraryPath
    try
        let descriptorGetter = Marshal.GetDelegateForFunctionPointer<ModuleDescriptorDelegate>(NativeLibrary.GetExport(library, "agentlang_module_descriptor"))
        let descriptorPointer = descriptorGetter.Invoke()
        let descriptor = Marshal.PtrToStructure<RawModuleDescriptor>(descriptorPointer)
        check "module descriptor matches ABI and runtime versions from independent fixture" (
            descriptor.AbiVersion = uint32 (fixtureRoot.GetProperty("moduleAbiVersion").GetInt32())
            && descriptor.RuntimeAbiVersion = uint32 (fixtureRoot.GetProperty("executionAbiVersion").GetInt32())
            && descriptor.StructSize = uint32 (moduleFixture.GetProperty("size").GetInt32())
            && descriptor.EntryCount = 3u)
        check "module descriptor exposes exact 32-byte deterministic fingerprint" (
            let nativeFingerprint = [| for index in 0 .. 31 -> Marshal.ReadByte(descriptorPointer, 16 + index) |] |> Convert.ToHexString |> fun hex -> hex.ToLowerInvariant()
            nativeFingerprint = optimization0.Fingerprint)

        let entryPointers =
            [| for index in 0 .. int descriptor.EntryCount - 1 ->
                   IntPtr.Add(descriptor.Entries, index * entryFixture.GetProperty("size").GetInt32()) |]
        let rawEntries = entryPointers |> Array.map Marshal.PtrToStructure<RawModuleEntry>
        let nativeNames = rawEntries |> Array.map (fun item -> Marshal.PtrToStringUTF8(item.Name))
        check "module descriptor exposes sorted names, IDs, signatures and execute functions" (
            nativeNames = [| "A.begin"; "B.resume-☃"; "C.failure" |]
            && (rawEntries |> Array.mapi (fun index item -> item.EntryId = uint32 index && item.StructSize = uint32 (entryFixture.GetProperty("size").GetInt32()) && item.Execute <> nativeint 0) |> Array.forall id)
            && rawEntries[0].InputCount = 0u && rawEntries[0].OutputCount = 1u
            && Marshal.ReadInt32(rawEntries[0].OutputTypeIds) = int NativeAbi.TypeIdInt
            && rawEntries[1].InputCount = 1u && rawEntries[1].OutputCount = 1u
            && Marshal.ReadInt32(rawEntries[1].InputTypeIds) = int NativeAbi.TypeIdInt
            && Marshal.ReadInt32(rawEntries[1].OutputTypeIds) = int NativeAbi.TypeIdInt)

        let programPointer = descriptor.Program
        let typeDescriptors = Marshal.ReadIntPtr(programPointer)
        let typeCount = uint32 (Marshal.ReadInt32(programPointer, 8))
        let recordTypeDescriptor = IntPtr.Add(typeDescriptors, 3 * Marshal.SizeOf<NativeTypeDescriptor>())
        let recordFieldTypeIds = Marshal.ReadIntPtr(recordTypeDescriptor, 8)
        let nativeTypeNames =
            [| for index in 0 .. int typeCount - 1 ->
                   Marshal.ReadIntPtr(descriptor.TypeNames, index * IntPtr.Size) |> Marshal.PtrToStringUTF8 |]
        check "module program descriptor and full type-name table use the shared verified type IDs" (
            typeCount = 4u
            && nativeTypeNames = [| "Int"; "Bool"; "Unit"; "NativePoint" |]
            && Marshal.ReadInt32(recordTypeDescriptor, 0) = int NativeAbi.TypeKindRecord
            && Marshal.ReadInt32(recordTypeDescriptor, 4) = 2
            && Marshal.ReadInt32(recordFieldTypeIds, 0) = int NativeAbi.TypeIdInt
            && Marshal.ReadInt32(recordFieldTypeIds, sizeof<uint32>) = int NativeAbi.TypeIdBool)

        let beginStatus, _, beginValues =
            invokeRawModuleEntry rawEntries[0].Execute [||] [||] (int rawEntries[0].OutputCount) (int rawEntries[0].WorkspaceCapacity)
        check "module entry A.begin executes through its typed descriptor" (beginStatus = NativeAbi.StatusSuccess && beginValues = [ 17L ])
        let resumeStatus, _, resumeValues =
            invokeRawModuleEntry rawEntries[1].Execute [| 102L |] [| NativeAbi.TypeIdInt |] (int rawEntries[1].OutputCount) (int rawEntries[1].WorkspaceCapacity)
        check "module entry B.resume reads its declared typed input and returns its verified output" (
            resumeStatus = NativeAbi.StatusSuccess && resumeValues = [ 103L ])

        let failureEntry = rawEntries[2]
        let failureDiagnostic = Marshal.PtrToStructure<RawModuleDiagnostic>(failureEntry.Diagnostics)
        check "immutable entry diagnostics preserve source text through UTF-8 C metadata" (
            failureEntry.DiagnosticCount > 0u
            && not (String.IsNullOrEmpty(Marshal.PtrToStringUTF8 failureDiagnostic.Code))
            && Marshal.PtrToStringUTF8(failureDiagnostic.Word) = "C.failure"
            && Marshal.PtrToStringUTF8(failureDiagnostic.File) = "failure-λ.agent")
    finally
        NativeLibrary.Free library

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

let private runFullSuite () =
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
        printStage "owning backend nominal Bool layouts, host codecs, and nesting"
        testOwningNominalBoolSlice ()
        printStage "owning backend frozen Bool refinements and mailbox policies"
        testOwningRefinedBoolSlice ()
        printStage "owning backend nominal Int layouts and host codecs"
        testOwningNominalIntSlice ()
        printStage "owning backend refined Int constructors and host admission"
        testOwningRefinedIntSlice ()
        printStage "owning backend refined String constructors, raw admission, and owner ranges"
        testOwningRefinedStringSlice ()
        printStage "owning backend unvalidated nominal String identity, bytes, and mailbox callbacks"
        testOwningNominalStringSlice ()
        testNominalScalarDiagnosticsAndRejections ()
        testNominalScalarDepth ()
        printStage "record construction, accessors, aliases, and equality"
        let sharingBody, sharingExpected = testRecordConformance ()
        printStage "record validator native parity and retained re-entry"
        testRecordValidatorConformance ()
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
        printStage "native module validation, immutable ABI metadata, and multi-entry compilation"
        testNativeModuleValidationBeforeTools ()
        testNativeModuleCompilation ()
        testCompilerRuntimeDiscovery ()
        printfn "AgentLang.Llvm.Tests: %d assertions passed; artifacts: %s" assertions artifactRoot
        0
    with ex ->
        eprintfn "%s" (ex.ToString())
        1

[<EntryPoint>]
let main args =
    if args |> Array.contains "--arena-lifetime" then
        try
            let assertions = global.AgentLang.Llvm.ArenaLifetimeTests.run ()
            printfn "AgentLang.Llvm.Tests arena lifetime: %d assertions passed" assertions
            0
        with ex ->
            eprintfn "%s" (ex.ToString())
            1
    elif args |> Array.contains "--module-only" then
        try
            printStage "native module validation, immutable ABI metadata, and multi-entry compilation"
            testNativeModuleValidationBeforeTools ()
            testNativeModuleCompilation ()
            printfn "AgentLang.Llvm.Tests module checks: %d assertions passed; artifacts: %s" assertions artifactRoot
            0
        with ex ->
            eprintfn "%s" (ex.ToString())
            1
    elif args |> Array.contains "--owning-refined-int" then
        try
            printStage "owning backend refined Int constructors and host admission"
            testOwningRefinedIntSlice ()
            printfn "AgentLang.Llvm.Tests owning refined Int checks: %d assertions passed; artifacts: %s" assertions artifactRoot
            0
        with ex ->
            eprintfn "%s" (ex.ToString())
            1
    elif args |> Array.contains "--owning-nominal-bool" then
        try
            printStage "owning backend nominal Bool layouts, host codecs, and nesting"
            testOwningNominalBoolSlice ()
            printfn "AgentLang.Llvm.Tests owning nominal Bool checks: %d assertions passed; artifacts: %s" assertions artifactRoot
            0
        with ex ->
            eprintfn "%s" (ex.ToString())
            1
    elif args |> Array.contains "--owning-refined-bool" then
        try
            printStage "owning backend frozen Bool refinements and mailbox policies"
            testOwningRefinedBoolSlice ()
            printfn "AgentLang.Llvm.Tests owning refined Bool checks: %d assertions passed; artifacts: %s" assertions artifactRoot
            0
        with ex ->
            eprintfn "%s" (ex.ToString())
            1
    elif args |> Array.contains "--owning-refined-string" then
        try
            printStage "owning backend refined String constructors, raw admission, and owner ranges"
            testOwningRefinedStringSlice ()
            printfn "AgentLang.Llvm.Tests owning refined String checks: %d assertions passed; artifacts: %s" assertions artifactRoot
            0
        with ex ->
            eprintfn "%s" (ex.ToString())
            1
    elif args |> Array.contains "--owning-nominal-string" then
        try
            printStage "owning backend unvalidated nominal String identity, bytes, and mailbox callbacks"
            testOwningNominalStringSlice ()
            printfn "AgentLang.Llvm.Tests owning nominal String checks: %d assertions passed; artifacts: %s" assertions artifactRoot
            0
        with ex ->
            eprintfn "%s" (ex.ToString())
            1
    elif args |> Array.contains "--owning-refined-mailbox" then
        try
            printStage "owning mailbox refined scalar active and inactive admission"
            testOwningRefinedMailboxVariants ()
            printfn "AgentLang.Llvm.Tests owning refined mailbox checks: %d assertions passed; artifacts: %s" assertions artifactRoot
            0
        with ex ->
            eprintfn "%s" (ex.ToString())
            1
    else
        runFullSuite ()
