module AgentLang.Llvm.Tests

open System
open System.IO
open System.Runtime.InteropServices
open System.Text.Json
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
        extraWords @ generatedWords
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

let private fixtureValues (name: string) =
    fixtureRoot.RootElement.GetProperty(name).EnumerateArray()
    |> Seq.map (fun value -> value.GetString())
    |> Seq.toList

let private fixtureError (name: string) : JsonElement =
    fixtureRoot.RootElement.GetProperty(name)

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

let private rawExecutionStatusAndSteps (native: NativeCompiledProgram) =
    let execute, library = rawExecute native.LibraryPath
    let context = Marshal.AllocHGlobal NativeAbi.ContextSize
    let outputs =
        if native.OutputCapacity = 0 then IntPtr.Zero
        else Marshal.AllocHGlobal(native.OutputCapacity * NativeAbi.SlotSize)
    let status = Marshal.AllocHGlobal sizeof<int32>
    try
        Marshal.WriteInt32(context, NativeAbi.ContextAbiVersionOffset, int NativeAbi.Version)
        Marshal.WriteInt32(context, NativeAbi.ContextStepsConsumedOffset, 0)
        Marshal.WriteInt32(context, NativeAbi.ContextErrorMetadataIdOffset, -1)
        Marshal.WriteInt32(context, NativeAbi.ContextReservedOffset, 0)
        Marshal.WriteInt64(context, NativeAbi.ContextErrorArgument0Offset, 0L)
        Marshal.WriteInt64(context, NativeAbi.ContextErrorArgument1Offset, 0L)
        Marshal.WriteInt32(status, NativeAbi.StatusInvalidRequest)
        execute.Invoke(nativeint context, nativeint outputs, native.OutputCapacity, nativeint status)
        Marshal.ReadInt32 status, Marshal.ReadInt32(context, NativeAbi.ContextStepsConsumedOffset)
    finally
        Marshal.FreeHGlobal status
        if outputs <> IntPtr.Zero then Marshal.FreeHGlobal outputs
        Marshal.FreeHGlobal context
        NativeLibrary.Free library

let private testLayoutAndRequestGuards multiOutputBody scratchBody =
    use native = compileNative "abi-guard" LlvmOptimization.O0 multiOutputBody
    let layoutHandle = NativeLibrary.Load native.LibraryPath
    try
        let capacityQuery = Marshal.GetDelegateForFunctionPointer<CapacityDelegate>(NativeLibrary.GetExport(layoutHandle, "agentlang_output_capacity"))
        check "exported scratch capacity query matches managed API" (capacityQuery.Invoke() = native.OutputCapacity)
        check "logical result count remains separate from scratch capacity" (native.OutputCount = 4 && native.OutputCapacity = 4)
        let layout = Marshal.GetDelegateForFunctionPointer<LayoutDelegate>(NativeLibrary.GetExport(layoutHandle, "agentlang_abi_layout"))
        let layoutBuffer = Marshal.AllocHGlobal(8 * sizeof<int64>)
        try
            layout.Invoke(nativeint layoutBuffer)
            let actualLayout = [ for index in 0 .. 7 -> Marshal.ReadInt64(layoutBuffer, index * sizeof<int64>) ]
            let expectedOffsets =
                [ NativeAbi.ContextAbiVersionOffset
                  NativeAbi.ContextStepsConsumedOffset
                  NativeAbi.ContextErrorMetadataIdOffset
                  NativeAbi.ContextReservedOffset
                  NativeAbi.ContextErrorArgument0Offset
                  NativeAbi.ContextErrorArgument1Offset ]
            let expectedLayout =
                [ int64 NativeAbi.ContextSize
                  int64 NativeAbi.ContextAlignment ]
                @ (expectedOffsets |> List.map int64)
            check "LLVM-reported context size/alignment/field offsets" (actualLayout = expectedLayout)
            check "managed context size matches ABI fixture" (Marshal.SizeOf<NativeExecutionContext>() = NativeAbi.ContextSize)
            let fieldNames = [ "AbiVersion"; "StepsConsumed"; "ErrorMetadataId"; "Reserved"; "ErrorArgument0"; "ErrorArgument1" ]
            let managedOffsets = fieldNames |> List.map (fun name -> Marshal.OffsetOf<NativeExecutionContext>(name).ToInt32())
            check "managed context field offsets match ABI fixture" (managedOffsets = expectedOffsets)
            let json = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "abi-v1.json")))
            let root = json.RootElement
            check "ABI fixture size matches native layout" (root.GetProperty("context").GetProperty("size").GetInt32() = int actualLayout[0])
            check "ABI fixture alignment matches native layout" (root.GetProperty("context").GetProperty("alignment").GetInt32() = int actualLayout[1])
            let fixtureOffsets =
                root.GetProperty("context").GetProperty("fields").EnumerateArray()
                |> Seq.map (fun field -> field.GetProperty("offset").GetInt32())
                |> Seq.toList
            check "ABI fixture field order matches native layout" (fixtureOffsets = expectedOffsets)
            let resultBuffer = root.GetProperty("resultBuffer")
            check "ABI fixture separates logical output count and scratch capacity" (
                resultBuffer.GetProperty("example").GetProperty("logicalOutputCount").GetInt32() = 1
                && resultBuffer.GetProperty("example").GetProperty("requiredScratchCapacity").GetInt32() = 2)
            let nominalSupport = root.GetProperty("nativeSemanticSupport").GetProperty("nominalScalars")
            let nominalBases = nominalSupport.GetProperty("supportedBaseTypes").EnumerateArray() |> Seq.map (fun value -> value.GetString()) |> Seq.toList
            check "ABI fixture documents unchanged i64 encoding for supported nominal scalars" (
                nominalBases = [ "Int"; "Bool" ]
                && nominalSupport.GetProperty("slotEncoding").GetString().Contains("existing i64 encoding"))
            json.Dispose()
        finally
            Marshal.FreeHGlobal layoutBuffer
    finally
        NativeLibrary.Free layoutHandle

    let checkRequestGuards name body =
        use guarded = compileNative name LlvmOptimization.O0 body
        check (name + " native scratch-capacity query") (
            let library = NativeLibrary.Load guarded.LibraryPath
            try
                let query = Marshal.GetDelegateForFunctionPointer<CapacityDelegate>(NativeLibrary.GetExport(library, "agentlang_output_capacity"))
                query.Invoke() = guarded.OutputCapacity
            finally
                NativeLibrary.Free library)
        if name = "abi-scratch-guard" then
            check "one logical result can require two scratch slots" (guarded.OutputCount = 1 && guarded.OutputCapacity = 2)
        let execute, executeHandle = rawExecute guarded.LibraryPath
        try
            let context = Marshal.AllocHGlobal NativeAbi.ContextSize
            let canaryCount = max 1 guarded.OutputCapacity + 1
            let canaries = Marshal.AllocHGlobal(canaryCount * NativeAbi.SlotSize)
            let status = Marshal.AllocHGlobal sizeof<int32>
            try
                let sentinel = 0x123456789ABCDEFL
                let callGuard version capacity =
                    Marshal.WriteInt32(context, NativeAbi.ContextAbiVersionOffset, version)
                    Marshal.WriteInt32(context, NativeAbi.ContextStepsConsumedOffset, 55)
                    Marshal.WriteInt32(context, NativeAbi.ContextErrorMetadataIdOffset, 77)
                    for index in 0 .. canaryCount - 1 do
                        Marshal.WriteInt64(canaries, index * NativeAbi.SlotSize, sentinel + int64 index)
                    Marshal.WriteInt32(status, 0)
                    execute.Invoke(nativeint context, nativeint canaries, capacity, nativeint status)
                    let statusValue = Marshal.ReadInt32 status
                    let unchanged =
                        [ 0 .. canaryCount - 1 ]
                        |> List.forall (fun index -> Marshal.ReadInt64(canaries, index * NativeAbi.SlotSize) = sentinel + int64 index)
                    check (name + " invalid request rejected before output writes") (statusValue = NativeAbi.StatusInvalidRequest && unchanged)
                    check (name + " invalid request leaves execution context untouched") (
                        Marshal.ReadInt32(context, NativeAbi.ContextStepsConsumedOffset) = 55
                        && Marshal.ReadInt32(context, NativeAbi.ContextErrorMetadataIdOffset) = 77)
                callGuard (int NativeAbi.Version + 1) guarded.OutputCapacity
                callGuard (int NativeAbi.Version) (guarded.OutputCapacity - 1)
            finally
                Marshal.FreeHGlobal status
                Marshal.FreeHGlobal canaries
                Marshal.FreeHGlobal context
        finally
            NativeLibrary.Free executeHandle

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
    check "scalar fixture records the native nominal support boundary" (
        supportedBases = [ "Int"; "Bool" ]
        && excluded = [ "records"; "Float"; "String"; "containers"; "effects" ])

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
        check ($"{optimization} validator Bool result has scratch capacity") (native.OutputCapacity = 1)
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

    let record =
        { Name = "NativeRecord"
          Fields = [ { Name = "value"; Type = TInt } ]
          SourceText = "record NativeRecord"
          Span = span "NativeRecord.agent" 1 }
    let recordConstructor = wordEntry "NativeRecord.create" [ TInt ] [ TNamed "NativeRecord" ] Set.empty []
    let recordConstructor = { recordConstructor with Builtin = Some(RecordConstructor "NativeRecord") }
    let recordContext = contextWithRecords [ recordConstructor ] [ record ]
    let recordBody = compileBody recordContext "unsupported-record" [
        Push(LInt 1L, span "unsupported-record.agent" 1)
        Call("NativeRecord.create", span "unsupported-record.agent" 2)
    ]
    let recordError = errorOf (fun () -> LlvmAot.emit recordBody |> ignore)
    check "record generated targets remain rejected" (recordError.Code = "IR_LLVM_UNSUPPORTED_TARGET")

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
        testLayoutAndRequestGuards multiOutput scratchBody

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
        testNominalScalarSupport ()
        testNominalScalarDiagnosticsAndRejections ()
        testNominalScalarDepth ()
        testNominalValidatorFuelLimit ()
        testRejectsEffectsAndUnsupportedUntakenBranches ()
        testCatalogTrustBoundary ()
        testCompilerRuntimeDiscovery ()
        printfn "AgentLang.Llvm.Tests: %d assertions passed; artifacts: %s" assertions artifactRoot
        0
    with ex ->
        eprintfn "%s" (ex.ToString())
        1
