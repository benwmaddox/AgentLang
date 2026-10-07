namespace AgentLang.Llvm

open System.Runtime.InteropServices

/// ABI-v2 execution context. The first 32 bytes preserve the published v1
/// diagnostic prefix; native code validates the version before reading the tail.
[<Struct; StructLayout(LayoutKind.Sequential, Pack = 8)>]
type NativeExecutionContext =
    val mutable AbiVersion: uint32
    val mutable StepsConsumed: uint32
    val mutable ErrorMetadataId: int32
    val mutable ReservedPrefix: int32
    val mutable ErrorArgument0: int64
    val mutable ErrorArgument1: int64
    val mutable Scratch: nativeint
    val mutable Retained: nativeint
    val mutable Workspace: nativeint
    val mutable WorkspaceCapacity: uint32
    val mutable ReservedTail: uint32
    new(abiVersion) =
        { AbiVersion = abiVersion
          StepsConsumed = 0u
          ErrorMetadataId = -1
          ReservedPrefix = 0
          ErrorArgument0 = 0L
          ErrorArgument1 = 0L
          Scratch = nativeint 0
          Retained = nativeint 0
          Workspace = nativeint 0
          WorkspaceCapacity = 0u
          ReservedTail = 0u }

[<Struct; StructLayout(LayoutKind.Sequential, Pack = 8)>]
type NativeArenaDescriptor =
    val mutable Data: nativeint
    val mutable ByteCapacity: uint32
    val mutable Used: uint32
    val mutable Nodes: nativeint
    val mutable NodeCapacity: uint32
    val mutable NodeCount: uint32
    val mutable Generation: uint32
    val mutable Flags: uint32
    val mutable Reserved: uint64

[<Struct; StructLayout(LayoutKind.Sequential, Pack = 8)>]
type NativeNode =
    val mutable TypeId: uint32
    val mutable FieldCount: uint32
    val mutable PayloadOffset: uint32
    val mutable PayloadBytes: uint32
    val mutable Mark: uint32
    val mutable Reserved: uint32
    val mutable ForwardHandle: uint64

[<Struct; StructLayout(LayoutKind.Sequential, Pack = 8)>]
type NativeTypeDescriptor =
    val mutable Kind: uint32
    val mutable FieldCount: uint32
    val mutable FieldTypes: nativeint

[<Struct; StructLayout(LayoutKind.Sequential, Pack = 8)>]
type NativeProgramDescriptor =
    val mutable Types: nativeint
    val mutable TypeCount: uint32
    val mutable Reserved: uint32

[<RequireQualifiedAccess>]
module NativeAbi =
    [<Literal>]
    let Version = 2u

    [<Literal>]
    let ContextSize = 64

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

    [<Literal>]
    let ContextScratchOffset = 32

    [<Literal>]
    let ContextRetainedOffset = 40

    [<Literal>]
    let ContextWorkspaceOffset = 48

    [<Literal>]
    let ContextWorkspaceCapacityOffset = 56

    [<Literal>]
    let ContextReservedTailOffset = 60

    [<Literal>]
    let ArenaSize = 48

    [<Literal>]
    let ArenaDataOffset = 0

    [<Literal>]
    let ArenaByteCapacityOffset = 8

    [<Literal>]
    let ArenaUsedOffset = 12

    [<Literal>]
    let ArenaNodesOffset = 16

    [<Literal>]
    let ArenaNodeCapacityOffset = 24

    [<Literal>]
    let ArenaNodeCountOffset = 28

    [<Literal>]
    let ArenaGenerationOffset = 32

    [<Literal>]
    let ArenaFlagsOffset = 36

    [<Literal>]
    let ArenaReservedOffset = 40

    [<Literal>]
    let NodeSize = 32

    [<Literal>]
    let NodeTypeIdOffset = 0

    [<Literal>]
    let NodeFieldCountOffset = 4

    [<Literal>]
    let NodePayloadOffsetOffset = 8

    [<Literal>]
    let NodePayloadBytesOffset = 12

    [<Literal>]
    let NodeMarkOffset = 16

    [<Literal>]
    let NodeReservedOffset = 20

    [<Literal>]
    let NodeForwardHandleOffset = 24

    [<Literal>]
    let TypeDescriptorSize = 16

    [<Literal>]
    let ProgramDescriptorSize = 16

    [<Literal>]
    let TypeKindInt = 1u

    [<Literal>]
    let TypeKindBool = 2u

    [<Literal>]
    let TypeKindUnit = 3u

    [<Literal>]
    let TypeKindRecord = 4u

    /// Type IDs are zero-based indexes in Program.types; TypeDesc.kind is a
    /// separate 1-based enum.
    [<Literal>]
    let TypeIdInt = 0u

    [<Literal>]
    let TypeIdBool = 1u

    [<Literal>]
    let TypeIdUnit = 2u

    [<Literal>]
    let StatusScratchCapacity = 3

    [<Literal>]
    let StatusRetainedCapacity = 4

    [<Literal>]
    let StatusInvalidReference = 5
