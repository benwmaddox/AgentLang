namespace AgentLang.FlowHttp

open System
open System.Text
open AgentLang

/// The per-worker store and the in-order response writer as host operations
/// (src/stdlib/store.flow, docs/WORKER-STORE-PLAN.md). Each worker owns one
/// zero-filled byte region, never shared and never grown; Flow lays out and
/// manages it (src/stdlib/worker-cache.flow). Offsets are bounds-checked and
/// Int accesses must be 8-byte aligned; a failed check is none/false.
/// response.write appends to the worker's response stream, which the host
/// sends before HttpResponse.body when the response sets `streamed`.
module Store =
    let capacityId = HostOperationId "store.capacity"
    let intId = HostOperationId "store.int"
    let intsId = HostOperationId "store.ints"
    let setIntId = HostOperationId "store.set-int"
    let writeId = HostOperationId "store.write"
    let bytesId = HostOperationId "store.bytes"
    let textId = HostOperationId "store.text"
    let matchesId = HostOperationId "store.matches"
    let hashId = HostOperationId "store.hash"
    let responseWriteId = HostOperationId "response.write"
    let responseWriteStoreId = HostOperationId "response.write-store"
    let responseWrittenId = HostOperationId "response.written"
    let responseDiscardId = HostOperationId "response.discard"

    /// tag, name, inputs, outputs, effects: the native manifest and the
    /// interpreter definitions come from this one table.
    let operations =
        [ 5u, capacityId, [ TInt ], [ TInt ], [ "store.read" ], "The worker store's size in bytes (0 when there is no store). The input is ignored."
          6u, intId, [ TInt ], [ TOption TInt ], [ "store.read" ], "The 8-byte Int at offset (8-aligned, in bounds), else none."
          7u, intsId, [ TInt; TInt ], [ TOption(TList TInt) ], [ "store.read" ], "count consecutive Ints from offset (8-aligned, in bounds, count 0..64), else none."
          8u, setIntId, [ TInt; TInt ], [ TBool ], [ "store.write" ], "Writes value as 8 bytes at offset (8-aligned, in bounds); false otherwise."
          9u, writeId, [ TInt; TString ], [ TBool ], [ "store.write" ], "Copies the String's bytes to offset when they fit; false otherwise."
          10u, bytesId, [ TInt; TInt ], [ TOption TBytes ], [ "store.read" ], "A copy of length bytes at offset, else none."
          11u, textId, [ TInt; TInt ], [ TOption TString ], [ "store.read" ], "length bytes at offset as a String (UTF-8 validated), else none."
          12u, matchesId, [ TInt; TString ], [ TBool ], [ "store.read" ], "True when the bytes at offset equal the String's bytes (in place, no copy)."
          13u, hashId, [ TString ], [ TInt ], [], "64-bit FNV-1a of the String's bytes, as an Int."
          14u, responseWriteId, [ TString ], [ TBool ], [ "response.write" ], "Appends the text to this request's response stream; false when it is full."
          15u, responseWriteStoreId, [ TInt; TInt ], [ TBool ], [ "response.write"; "store.read" ], "Appends length store bytes at offset to the response stream (one copy); false when out of bounds or full."
          16u, responseWrittenId, [ TInt ], [ TString ], [ "response.write" ], "The response stream so far, as text (for tests). The input is ignored."
          17u, responseDiscardId, [ TInt ], [ TInt ], [ "response.write" ], "Empties the response stream; returns the bytes dropped. The input is ignored." ]

    let operationIds = operations |> List.map (fun (_, id, _, _, _, _) -> id) |> Set.ofList

    let manifest : Map<HostOperationId, HostOperationDefinition> =
        operations
        |> List.map (fun (_, (HostOperationId name as id), inputs, outputs, effects, doc) ->
            id, { Id = id; WordName = name; Inputs = inputs; Outputs = outputs; Effects = Set.ofList effects; Documentation = doc })
        |> Map.ofList

    let nativeOperations =
        operations
        |> List.map (fun (tag, HostOperationId name, inputs, outputs, effects, _) ->
            tag, name, inputs |> List.map Types.format, outputs |> List.map Types.format, effects)

    /// One worker's store and response stream (the interpreter's "worker").
    type WorkerState(capacity: int, streamCapacity: int) =
        let region = Array.zeroCreate<byte> capacity
        let stream = Collections.Generic.List<byte>()
        member _.Region = region
        member _.Stream = stream
        member _.StreamCapacity = streamCapacity
        /// The host starts each request with an empty stream.
        member _.ResetStream() = stream.Clear()

    let private inRange (state: WorkerState) (offset: int64) (length: int64) =
        offset >= 0L && length >= 0L && offset <= int64 state.Region.Length && length <= int64 state.Region.Length - offset

    let private aligned (state: WorkerState) (offset: int64) = offset % 8L = 0L && inRange state offset 8L

    let fnv1a (data: byte[]) =
        let mutable hash = 14695981039346656037UL
        for b in data do
            hash <- (hash ^^^ uint64 b) * 1099511628211UL
        int64 hash

    let private strictUtf8 = UTF8Encoding(false, true)

    let createProvider (state: WorkerState) : HostOperationProvider =
        let region = state.Region
        let invoke id (arguments: Value list) =
            match arguments with
            | [ IntValue _ ] when id = capacityId -> Ok [ IntValue(int64 region.Length) ]
            | [ IntValue offset ] when id = intId ->
                let value = if aligned state offset then Some(IntValue(BitConverter.ToInt64(region, int offset))) else None
                Ok [ OptionValue(TInt, value) ]
            | [ IntValue offset; IntValue count ] when id = intsId ->
                let value =
                    if count >= 0L && count <= 64L && offset % 8L = 0L && inRange state offset (count * 8L) then
                        Some(ListValue(TInt, [ for i in 0 .. int count - 1 -> IntValue(BitConverter.ToInt64(region, int offset + i * 8)) ]))
                    else None
                Ok [ OptionValue(TList TInt, value) ]
            | [ IntValue offset; IntValue value ] when id = setIntId ->
                if aligned state offset then
                    BitConverter.TryWriteBytes(Span<byte>(region, int offset, 8), value) |> ignore
                    Ok [ BoolValue true ]
                else Ok [ BoolValue false ]
            | [ IntValue offset; StringValue text ] when id = writeId ->
                let data = Encoding.UTF8.GetBytes text
                if inRange state offset (int64 data.Length) then
                    Buffer.BlockCopy(data, 0, region, int offset, data.Length)
                    Ok [ BoolValue true ]
                else Ok [ BoolValue false ]
            | [ IntValue offset; IntValue length ] when id = bytesId ->
                let value = if inRange state offset length then Some(BytesValue(Array.sub region (int offset) (int length))) else None
                Ok [ OptionValue(TBytes, value) ]
            | [ IntValue offset; IntValue length ] when id = textId ->
                let value =
                    if inRange state offset length then
                        try Some(StringValue(strictUtf8.GetString(region, int offset, int length))) with :? ArgumentException -> None
                    else None
                Ok [ OptionValue(TString, value) ]
            | [ IntValue offset; StringValue text ] when id = matchesId ->
                let data = Encoding.UTF8.GetBytes text
                Ok [ BoolValue(inRange state offset (int64 data.Length) && Span<byte>(region, int offset, data.Length).SequenceEqual(ReadOnlySpan<byte>(data))) ]
            | [ StringValue text ] when id = hashId -> Ok [ IntValue(fnv1a (Encoding.UTF8.GetBytes text)) ]
            | [ StringValue text ] when id = responseWriteId ->
                let data = Encoding.UTF8.GetBytes text
                if state.Stream.Count + data.Length <= state.StreamCapacity then
                    state.Stream.AddRange data
                    Ok [ BoolValue true ]
                else Ok [ BoolValue false ]
            | [ IntValue offset; IntValue length ] when id = responseWriteStoreId ->
                if inRange state offset length && int64 state.Stream.Count + length <= int64 state.StreamCapacity then
                    state.Stream.AddRange(ArraySegment<byte>(region, int offset, int length))
                    Ok [ BoolValue true ]
                else Ok [ BoolValue false ]
            | [ IntValue _ ] when id = responseWrittenId ->
                // The stream holds whole Strings and validated store ranges; decode leniently.
                Ok [ StringValue(Encoding.UTF8.GetString(state.Stream.ToArray())) ]
            | [ IntValue _ ] when id = responseDiscardId ->
                let dropped = state.Stream.Count
                state.Stream.Clear()
                Ok [ IntValue(int64 dropped) ]
            | _ when operationIds.Contains id -> Error HostOperationFailure.ProviderContractMismatch
            | _ -> Error HostOperationFailure.MissingHandler
        { OperationIds = operationIds; Invoke = invoke; Dispose = ignore }

    /// The store size attached tests get (fresh per test).
    let testCapacity = 262144

    /// The response stream capacity, matching the native --retained-mb default.
    let streamCapacity = 4 * 1024 * 1024
