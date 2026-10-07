namespace AgentLang.Llvm

open System.Runtime.InteropServices

/// Published Windows x64 ABI-v1 context. The native backend reads and writes
/// these fields by fixed byte offset; keep the native-conformance ABI fixture
/// aligned with this layout. Callers must provide a non-null 8-byte-aligned
/// context, a non-null 4-byte-aligned status pointer, and aligned scratch storage.
[<Struct; StructLayout(LayoutKind.Sequential, Pack = 8)>]
type NativeExecutionContext =
    val mutable AbiVersion: uint32
    val mutable StepsConsumed: uint32
    val mutable ErrorMetadataId: int32
    val mutable Reserved: int32
    val mutable ErrorArgument0: int64
    val mutable ErrorArgument1: int64
    new(abiVersion) =
        { AbiVersion = abiVersion
          StepsConsumed = 0u
          ErrorMetadataId = -1
          Reserved = 0
          ErrorArgument0 = 0L
          ErrorArgument1 = 0L }

[<RequireQualifiedAccess>]
module NativeAbi =
    [<Literal>]
    let Version = 1u

    [<Literal>]
    let ContextSize = 32

    [<Literal>]
    let ContextAlignment = 8

    [<Literal>]
    let SlotSize = 8

    [<Literal>]
    let StatusSuccess = 0

    [<Literal>]
    let StatusDiagnostic = 1

    [<Literal>]
    let StatusInvalidRequest = 2

    [<Literal>]
    let ContextAbiVersionOffset = 0

    [<Literal>]
    let ContextStepsConsumedOffset = 4

    [<Literal>]
    let ContextErrorMetadataIdOffset = 8

    [<Literal>]
    let ContextReservedOffset = 12

    [<Literal>]
    let ContextErrorArgument0Offset = 16

    [<Literal>]
    let ContextErrorArgument1Offset = 24
