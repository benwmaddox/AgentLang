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

let private contextWith (extraWords: WordEntry list) : Compiler.IrLoweringContext =
    let words =
        extraWords
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
        testRejectsEffectsAndUnsupportedUntakenBranches ()
        testCatalogTrustBoundary ()
        testCompilerRuntimeDiscovery ()
        printfn "AgentLang.Llvm.Tests: %d assertions passed; artifacts: %s" assertions artifactRoot
        0
    with ex ->
        eprintfn "%s" (ex.ToString())
        1
