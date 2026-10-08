namespace AgentLang.Llvm

open System
open System.Collections.Generic
open System.IO
open System.Runtime.InteropServices
open System.Threading
open AgentLang

type NativeExecutionResult =
    { Values: Value list
      StepsConsumed: int }

[<RequireQualifiedAccess>]
module internal NativeGeneration =
    let mutable private lastIssued = 0L

    let next () =
        let value = Interlocked.Increment(&lastIssued)
        if value <= 0L || uint64 value > uint64 UInt32.MaxValue then
            invalidOp "The native arena generation space is exhausted and cannot wrap."
        uint32 value

[<Sealed>]
type NativeResourceLimitException internal
    (code: string, arena: string, requiredBytes: int64, requiredNodes: int64, availableBytes: int, availableNodes: int) =
    inherit InvalidOperationException(
        $"{code}: native {arena} arena requires {requiredBytes} bytes and {requiredNodes} nodes; " +
        $"capacity is {availableBytes} bytes and {availableNodes} nodes.")

    member _.Code = code
    member _.Arena = arena
    member _.RequiredBytes = requiredBytes
    member _.RequiredNodes = requiredNodes
    member _.AvailableBytes = availableBytes
    member _.AvailableNodes = availableNodes

type internal NativeTypeDefinitionMetadata =
    | NativeScalarMetadata of TypeName: string * BaseTypeId: uint32
    | NativeRecordMetadata of TypeName: string * Fields: (string * uint32) list

type internal NativeValueTypeMetadata =
    { TypeId: uint32
      Kind: uint32
      ValueType: IrType
      Children: IrType list
      Definition: NativeTypeDefinitionMetadata option }

/// Owns the unmanaged ABI descriptor and its optional byte and node arrays.
/// Callers serialize access through SyncRoot when reusing an owner.
[<Sealed>]
type internal NativeArenaOwner(byteCapacity: int, nodeCapacity: int) =
    let gate = obj ()
    let mutable disposed = false
    let mutable descriptor = IntPtr.Zero
    let mutable data = IntPtr.Zero
    let mutable nodes = IntPtr.Zero

    let ensureOpen () =
        if disposed then raise (ObjectDisposedException(nameof NativeArenaOwner))

    let writeInitialDescriptor () =
        Marshal.WriteIntPtr(descriptor, NativeAbi.ArenaDataOffset, data)
        Marshal.WriteInt32(descriptor, NativeAbi.ArenaByteCapacityOffset, byteCapacity)
        Marshal.WriteInt32(descriptor, NativeAbi.ArenaUsedOffset, 0)
        Marshal.WriteIntPtr(descriptor, NativeAbi.ArenaNodesOffset, nodes)
        Marshal.WriteInt32(descriptor, NativeAbi.ArenaNodeCapacityOffset, nodeCapacity)
        Marshal.WriteInt32(descriptor, NativeAbi.ArenaNodeCountOffset, 0)
        Marshal.WriteInt32(descriptor, NativeAbi.ArenaGenerationOffset, 0)
        Marshal.WriteInt32(descriptor, NativeAbi.ArenaFlagsOffset, 0)
        Marshal.WriteInt64(descriptor, NativeAbi.ArenaReservedOffset, 0L)

    do
        if byteCapacity < 0 then invalidArg (nameof byteCapacity) "Arena byte capacity cannot be negative."
        if nodeCapacity < 0 || int64 nodeCapacity * int64 NativeAbi.NodeSize > int64 Int32.MaxValue then
            invalidArg (nameof nodeCapacity) "Arena node capacity is negative or too large."
        try
            descriptor <- Marshal.AllocHGlobal NativeAbi.ArenaSize
            data <- if byteCapacity = 0 then IntPtr.Zero else Marshal.AllocHGlobal byteCapacity
            let directoryBytes = nodeCapacity * NativeAbi.NodeSize
            nodes <- if directoryBytes = 0 then IntPtr.Zero else Marshal.AllocHGlobal directoryBytes
            writeInitialDescriptor ()
        with _ ->
            if nodes <> IntPtr.Zero then Marshal.FreeHGlobal nodes
            if data <> IntPtr.Zero then Marshal.FreeHGlobal data
            if descriptor <> IntPtr.Zero then Marshal.FreeHGlobal descriptor
            nodes <- IntPtr.Zero
            data <- IntPtr.Zero
            descriptor <- IntPtr.Zero
            reraise ()

    member _.ByteCapacity = byteCapacity
    member _.NodeCapacity = nodeCapacity
    member _.DescriptorPointer = lock gate (fun () -> ensureOpen (); descriptor)
    member _.DataPointer = lock gate (fun () -> ensureOpen (); data)
    member _.NodePointer = lock gate (fun () -> ensureOpen (); nodes)
    member _.SyncRoot = gate

    member _.Initialize(generation: uint32) =
        lock gate (fun () ->
            ensureOpen ()
            if generation = 0u then invalidArg (nameof generation) "Arena generations must be nonzero."
            Marshal.WriteInt32(descriptor, NativeAbi.ArenaUsedOffset, 0)
            Marshal.WriteInt32(descriptor, NativeAbi.ArenaNodeCountOffset, 0)
            Marshal.WriteInt32(descriptor, NativeAbi.ArenaGenerationOffset, int generation)
            Marshal.WriteInt32(descriptor, NativeAbi.ArenaFlagsOffset, 0)
            Marshal.WriteInt64(descriptor, NativeAbi.ArenaReservedOffset, 0L))

    member _.Reset(poison: bool) =
        lock gate (fun () ->
            ensureOpen ()
            if poison then
                if data <> IntPtr.Zero then
                    let fill = Array.create byteCapacity 0xA5uy
                    Marshal.Copy(fill, 0, data, fill.Length)
                if nodes <> IntPtr.Zero then
                    let directoryBytes = nodeCapacity * NativeAbi.NodeSize
                    let fill = Array.create directoryBytes 0xDDuy
                    Marshal.Copy(fill, 0, nodes, fill.Length)
            Marshal.WriteInt32(descriptor, NativeAbi.ArenaUsedOffset, 0)
            Marshal.WriteInt32(descriptor, NativeAbi.ArenaNodeCountOffset, 0)
            Marshal.WriteInt32(descriptor, NativeAbi.ArenaGenerationOffset, 0)
            Marshal.WriteInt32(descriptor, NativeAbi.ArenaFlagsOffset, 0)
            Marshal.WriteInt64(descriptor, NativeAbi.ArenaReservedOffset, 0L))

    member _.UsedBytes =
        lock gate (fun () -> ensureOpen (); Marshal.ReadInt32(descriptor, NativeAbi.ArenaUsedOffset))

    member _.UsedNodeCount =
        lock gate (fun () -> ensureOpen (); Marshal.ReadInt32(descriptor, NativeAbi.ArenaNodeCountOffset))

    member _.Generation =
        lock gate (fun () -> ensureOpen (); uint32 (Marshal.ReadInt32(descriptor, NativeAbi.ArenaGenerationOffset)))

    member _.ReadNode(nodeIndex: int) =
        lock gate (fun () ->
            ensureOpen ()
            let count = Marshal.ReadInt32(descriptor, NativeAbi.ArenaNodeCountOffset)
            if nodeIndex < 0 || nodeIndex >= count then invalidArg (nameof nodeIndex) "Node index is outside the used arena directory."
            Marshal.PtrToStructure<NativeNode>(IntPtr.Add(nodes, nodeIndex * NativeAbi.NodeSize)))

    member _.ReadSlot(byteOffset: int) =
        lock gate (fun () ->
            ensureOpen ()
            let used = Marshal.ReadInt32(descriptor, NativeAbi.ArenaUsedOffset)
            if byteOffset < 0 || byteOffset % NativeAbi.SlotSize <> 0 || int64 byteOffset + int64 NativeAbi.SlotSize > int64 used then
                invalidArg (nameof byteOffset) "Slot offset is outside the used arena data."
            Marshal.ReadInt64(data, byteOffset))

    member _.CopyData(usedBytes: int) =
        lock gate (fun () ->
            ensureOpen ()
            if usedBytes < 0 || usedBytes > byteCapacity then invalidArg (nameof usedBytes) "Used arena bytes are outside the allocated capacity."
            let snapshot = Array.zeroCreate<byte> usedBytes
            if usedBytes > 0 then Marshal.Copy(data, snapshot, 0, usedBytes)
            snapshot)

    member _.CopyNodes(nodeCount: int) =
        lock gate (fun () ->
            ensureOpen ()
            if nodeCount < 0 || nodeCount > nodeCapacity then invalidArg (nameof nodeCount) "Used node count is outside the allocated capacity."
            Array.init nodeCount (fun index ->
                Marshal.PtrToStructure<NativeNode>(IntPtr.Add(nodes, index * NativeAbi.NodeSize))))

    member private _.DisposeCore() =
        if not disposed then
            if nodes <> IntPtr.Zero then Marshal.FreeHGlobal nodes
            if data <> IntPtr.Zero then Marshal.FreeHGlobal data
            if descriptor <> IntPtr.Zero then Marshal.FreeHGlobal descriptor
            nodes <- IntPtr.Zero
            data <- IntPtr.Zero
            descriptor <- IntPtr.Zero
            disposed <- true

    interface IDisposable with
        member this.Dispose() = lock gate this.DisposeCore

[<Sealed>]
type NativeScratchArena(byteCapacity: int, nodeCapacity: int) =
    let owner = new NativeArenaOwner(byteCapacity, nodeCapacity)

    member _.ByteCapacity = owner.ByteCapacity
    member _.NodeCapacity = owner.NodeCapacity
    member _.Clear() = owner.Reset(false)
    member _.PoisonAndClear() = owner.Reset(true)
    member internal _.Owner = owner

    interface IDisposable with
        member _.Dispose() = (owner :> IDisposable).Dispose()

/// Per-call capacity overrides and an optional caller-owned scratch arena.
/// Omitted capacities use bounded defaults when the compiled body uses records.
type NativeExecutionOptions =
    { ScratchByteCapacity: int option
      ScratchNodeCapacity: int option
      RetainedByteCapacity: int option
      RetainedNodeCapacity: int option
      ScratchArena: NativeScratchArena option }

[<RequireQualifiedAccess>]
module NativeExecutionOptions =
    let defaults =
        { ScratchByteCapacity = None
          ScratchNodeCapacity = None
          RetainedByteCapacity = None
          RetainedNodeCapacity = None
          ScratchArena = None }

[<Sealed>]
type NativeRetainedResult internal
    (programIdentity: VerifiedIrProgram,
     stepsConsumed: int,
     rootValues: int64 array,
     rootTypeIds: uint32 array,
     retainedOwner: NativeArenaOwner,
     typeMetadata: NativeValueTypeMetadata array) =
    let gate = obj ()
    let mutable disposed = false
    let mutable decodedValues: Value list option = None
    // Each result owns its small root metadata array. The compiled program's
    // immutable type table may be shared by any number of live results.
    let frozenRootTypeIds = Array.copy rootTypeIds

    let ensureOpen () =
        if disposed then raise (ObjectDisposedException(nameof NativeRetainedResult))

    let decode () =
        lock gate (fun () ->
            ensureOpen ()
            match decodedValues with
            | Some values -> values
            | None ->
                if rootValues.Length <> frozenRootTypeIds.Length then
                    raise (InvalidDataException("Native retained roots do not match their frozen type metadata."))
                let cache = Dictionary<uint64, Value>()
                let active = HashSet<uint64>()
                let getType typeId =
                    if uint64 typeId >= uint64 typeMetadata.Length then
                        raise (InvalidDataException($"Native retained value references unknown type id {typeId}."))
                    typeMetadata[int typeId]
                let rec decodeValue (typeId: uint32) (bits: int64) =
                    let metadata = getType typeId
                    match metadata.Definition with
                    | Some(NativeScalarMetadata(typeName, baseTypeId)) ->
                        NamedValue(typeName, decodeValue baseTypeId bits)
                    | Some(NativeRecordMetadata(typeName, fields)) ->
                        decodeRecord metadata typeName fields bits
                    | None ->
                        match metadata.Kind with
                        | NativeAbi.TypeKindInt -> IntValue bits
                        | NativeAbi.TypeKindBool when bits = 0L -> BoolValue false
                        | NativeAbi.TypeKindBool when bits = 1L -> BoolValue true
                        | NativeAbi.TypeKindBool -> raise (InvalidDataException($"Native Bool value was not encoded as 0 or 1: {bits}."))
                        | NativeAbi.TypeKindUnit when bits = 0L -> UnitValue
                        | NativeAbi.TypeKindUnit -> raise (InvalidDataException($"Native Unit value was not encoded as 0: {bits}."))
                        | kind -> raise (InvalidDataException($"Native retained value has unsupported type kind {kind}."))
                and decodeRecord metadata typeName fields bits =
                    let handle = uint64 bits
                    match cache.TryGetValue handle with
                    | true, value -> value
                    | false, _ ->
                        let generation = uint32 (handle >>> 32)
                        let nodeIndex = uint32 handle
                        let retainedGeneration = retainedOwner.Generation
                        let retainedNodeCount = retainedOwner.UsedNodeCount
                        let retainedUsedBytes = retainedOwner.UsedBytes
                        if generation = 0u || generation <> retainedGeneration || nodeIndex = 0u || uint64 nodeIndex > uint64 retainedNodeCount then
                            raise (InvalidDataException("Native retained record handle has an invalid generation or node index."))
                        if not (active.Add handle) then
                            raise (InvalidDataException("Native retained graph contains a cycle."))
                        try
                            let node = retainedOwner.ReadNode(int nodeIndex - 1)
                            if node.TypeId <> metadata.TypeId || uint64 node.FieldCount <> uint64 fields.Length ||
                               uint64 node.PayloadBytes <> uint64 fields.Length * uint64 NativeAbi.SlotSize ||
                               node.PayloadOffset % uint32 NativeAbi.SlotSize <> 0u ||
                               uint64 node.PayloadOffset + uint64 node.PayloadBytes > uint64 retainedUsedBytes then
                                raise (InvalidDataException("Native retained node does not match its frozen record layout."))
                            let decodedFields =
                                fields
                                |> List.mapi (fun fieldIndex (fieldName, fieldTypeId) ->
                                    let offset = int node.PayloadOffset + fieldIndex * NativeAbi.SlotSize
                                    let raw = retainedOwner.ReadSlot offset
                                    fieldName, decodeValue fieldTypeId raw)
                            let value = RecordValue(typeName, Map.ofList decodedFields)
                            cache.Add(handle, value)
                            value
                        finally
                            active.Remove handle |> ignore
                let values =
                    Array.map2 decodeValue frozenRootTypeIds rootValues
                    |> Array.toList
                decodedValues <- Some values
                values)

    member internal _.WithBorrow(expectedProgram: VerifiedIrProgram, executionName: string, action: NativeArenaOwner -> int64 array -> uint32 array -> 'T) =
        lock gate (fun () ->
            if disposed then
                Diagnostics.raiseError "IR_BACKEND_ENTRY_OWNER_DISPOSED" "The retained input owner has been disposed." (Some executionName) None
                    [ "live retained input owner" ] [ "disposed" ]
            if not (Object.ReferenceEquals(programIdentity, expectedProgram)) then
                Diagnostics.raiseError "IR_BACKEND_ENTRY_PROGRAM_MISMATCH" "The retained input belongs to a different verified-program instance." (Some executionName) None
                    [ "same VerifiedIrProgram instance" ] [ "different program instance" ]
            action retainedOwner rootValues frozenRootTypeIds)

    member _.StepsConsumed = stepsConsumed
    member _.Values = decode ()
    member _.Decode() = decode ()
    member _.RetainedByteCount = retainedOwner.UsedBytes
    member _.RetainedNodeCount = retainedOwner.UsedNodeCount

    interface IDisposable with
        member _.Dispose() =
            lock gate (fun () ->
                if not disposed then
                    Array.Clear(rootValues, 0, rootValues.Length)
                    Array.Clear(frozenRootTypeIds, 0, frozenRootTypeIds.Length)
                    (retainedOwner :> IDisposable).Dispose()
                    decodedValues <- None
                    disposed <- true)
