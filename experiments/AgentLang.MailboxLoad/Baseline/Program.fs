module AgentLang.MailboxLoad.Baseline.Program

open System
open System.Collections.Generic
open System.Globalization
open System.IO
open System.Net
open System.Net.Sockets
open System.Text
open System.Text.Json
open System.Threading
open System.Threading.Channels
open System.Threading.Tasks
open System.Diagnostics

type private Options =
    { Port: int
      Rate: int
      PayloadBytes: int
      DelayMs: int
      WarmupMs: int
      DurationMs: int
      Output: string }

type private State =
    { Attempted: int64
      Completed: int64
      Latest: string }

type private Continuation =
    { Request: string }

type private Completion =
    { Mailbox: int
      ScheduledAt: int64
      Response: byte array option
      Failure: string option
      WasCancelled: bool }

type private PhaseResult =
    { Offered: int64
      Admitted: int64
      Rejected: int64
      MissedArrivals: int64
      Completed: int64
      CompletedWithinWindow: int64
      Errors: int64
      TimedOut: int64
      PendingAtEnd: int64
      PeakPending: int64
      MaxDispatchLatenessMicroseconds: int64
      LatencyMicroseconds: int64 array
      AdmissionCounts: int64 array
      CompletionCounts: int64 array
      States: State array }

let private mailboxCount = 16
let private catchUpBatchSize = 64
let private drainDeadlineMs = 5000

let private usage =
    "Usage: AgentLang.MailboxLoad.Baseline --port PORT --rate RATE --payload-bytes BYTES --delay-ms DELAY --warmup-ms MS --duration-ms MS --output ABSOLUTE_JSON_PATH"

let private parseInteger (optionName: string) (value: string) (minimum: int) (maximum: int) =
    let mutable parsed = 0
    if not (Int32.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, &parsed)) then
        invalidArg "arguments" $"{optionName} must be an integer from {minimum} through {maximum}."
    if parsed < minimum || parsed > maximum then
        invalidArg "arguments" $"{optionName} must be an integer from {minimum} through {maximum}."
    parsed

let private parseOptions (arguments: string array) =
    let allowed =
        HashSet<string>(
            [ "--port"
              "--rate"
              "--payload-bytes"
              "--delay-ms"
              "--warmup-ms"
              "--duration-ms"
              "--output" ],
            StringComparer.Ordinal)
    let values = Dictionary<string, string>(StringComparer.Ordinal)
    let mutable index = 0

    while index < arguments.Length do
        let optionName = arguments[index]
        if not (allowed.Contains optionName) then invalidArg "arguments" $"Unknown option '{optionName}'."
        if values.ContainsKey optionName then invalidArg "arguments" $"{optionName} may be supplied only once."
        if index + 1 >= arguments.Length then invalidArg "arguments" $"Missing value for {optionName}."
        values.Add(optionName, arguments[index + 1])
        index <- index + 2

    let required optionName =
        match values.TryGetValue optionName with
        | true, value -> value
        | false, _ -> invalidArg "arguments" $"{optionName} is required."

    let outputValue = required "--output"
    if not (Path.IsPathFullyQualified outputValue) then
        invalidArg "arguments" "--output must be an absolute path."

    { Port = parseInteger "--port" (required "--port") 1 65535
      Rate = parseInteger "--rate" (required "--rate") 1 64000
      PayloadBytes = parseInteger "--payload-bytes" (required "--payload-bytes") 1 4096
      DelayMs = parseInteger "--delay-ms" (required "--delay-ms") 0 1000
      WarmupMs = parseInteger "--warmup-ms" (required "--warmup-ms") 0 10000
      DurationMs = parseInteger "--duration-ms" (required "--duration-ms") 100 30000
      Output = Path.GetFullPath outputValue }

let private initialize seed =
    { Attempted = 0L
      Completed = 0L
      Latest = seed }

let private beginRequest (state: State) request =
    let nextState =
        { Attempted = state.Attempted + 1L
          Completed = state.Completed
          Latest = state.Latest }
    let continuation = { Request = request }
    nextState, continuation

let private resume (state: State) (continuation: Continuation) message =
    { Attempted = state.Attempted
      Completed = state.Completed + 1L
      Latest = continuation.Request + message }

let private writeUInt32LittleEndian (buffer: byte array) offset (value: uint32) =
    buffer[offset] <- byte (value &&& 0xFFu)
    buffer[offset + 1] <- byte ((value >>> 8) &&& 0xFFu)
    buffer[offset + 2] <- byte ((value >>> 16) &&& 0xFFu)
    buffer[offset + 3] <- byte ((value >>> 24) &&& 0xFFu)

let private readUInt32LittleEndian (buffer: byte array) offset =
    uint32 buffer[offset]
    ||| (uint32 buffer[offset + 1] <<< 8)
    ||| (uint32 buffer[offset + 2] <<< 16)
    ||| (uint32 buffer[offset + 3] <<< 24)

let private sendExactly (socket: Socket) (buffer: byte array) cancellationToken =
    task {
        let mutable offset = 0
        while offset < buffer.Length do
            let! sent = socket.SendAsync(buffer.AsMemory(offset), SocketFlags.None, cancellationToken)
            if sent = 0 then raise (IOException "The provider accepted no request bytes.")
            offset <- offset + sent
    }

let private receiveExactly (socket: Socket) (buffer: byte array) cancellationToken =
    task {
        let mutable offset = 0
        while offset < buffer.Length do
            let! received = socket.ReceiveAsync(buffer.AsMemory(offset), SocketFlags.None, cancellationToken)
            if received = 0 then raise (EndOfStreamException "The provider closed before the complete response arrived.")
            offset <- offset + received
    }

let private exchange (socket: Socket) delayMs (payload: byte array) cancellationToken =
    task {
        let header = Array.zeroCreate<byte> 8
        writeUInt32LittleEndian header 0 (uint32 delayMs)
        writeUInt32LittleEndian header 4 (uint32 payload.Length)
        do! sendExactly socket header cancellationToken
        do! sendExactly socket payload cancellationToken

        let responseHeader = Array.zeroCreate<byte> 4
        do! receiveExactly socket responseHeader cancellationToken
        let responseLength = readUInt32LittleEndian responseHeader 0
        if responseLength <> uint32 payload.Length then
            raise (InvalidDataException $"Provider response length {responseLength} did not match request length {payload.Length}.")

        let response = Array.zeroCreate<byte> payload.Length
        do! receiveExactly socket response cancellationToken
        return response
    }

let private connectSockets port =
    task {
        let sockets = Array.init mailboxCount (fun _ -> new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp))
        try
            for socket in sockets do
                socket.NoDelay <- true
                do! socket.ConnectAsync(IPEndPoint(IPAddress.Loopback, port), CancellationToken.None)
            return sockets
        with error ->
            for socket in sockets do socket.Dispose()
            return raise error
    }

let private ticksForArrival sequence rate =
    (sequence * int64 Stopwatch.Frequency) / int64 rate

let private ticksForMilliseconds milliseconds =
    int64 milliseconds * int64 Stopwatch.Frequency / 1000L

let private microsecondsFromTicks ticks =
    max 0L (ticks * 1_000_000L / int64 Stopwatch.Frequency)

let private runPhase (rate: int) (durationMs: int) (delayMs: int) (payload: byte array) (sockets: Socket array) (measured: bool) =
    task {
        let states = Array.init mailboxCount (fun _ -> initialize String.Empty)
        let continuations: Continuation option array = Array.create mailboxCount None
        let inFlight = Array.zeroCreate<bool> mailboxCount
        let admissionCounts = Array.zeroCreate<int64> mailboxCount
        let completionCounts = Array.zeroCreate<int64> mailboxCount
        let latencies = ResizeArray<int64>()
        let channelOptions = BoundedChannelOptions(mailboxCount)
        channelOptions.SingleReader <- true
        channelOptions.SingleWriter <- false
        channelOptions.FullMode <- BoundedChannelFullMode.Wait
        let completions = Channel.CreateBounded<Completion>(channelOptions)
        use ioCancellation = new CancellationTokenSource()

        let mutable offered = 0L
        let mutable admitted = 0L
        let mutable rejected = 0L
        let mutable missedArrivals = 0L
        let mutable completed = 0L
        let mutable completedWithinWindow = 0L
        let mutable errors = 0L
        let mutable timedOut = 0L
        let mutable active = 0L
        let mutable peakPending = 0L
        let mutable maxDispatchLatenessMicroseconds = 0L
        let mutable sequence = 0L

        let phaseStart = Stopwatch.GetTimestamp()
        let windowDeadline = phaseStart + ticksForMilliseconds durationMs
        let scheduledCount = (int64 rate * int64 durationMs + 999L) / 1000L

        let processCompletion (completion: Completion) =
            let mailbox = completion.Mailbox
            if mailbox < 0 || mailbox >= mailboxCount || not inFlight[mailbox] then
                errors <- errors + 1L
            else
                inFlight[mailbox] <- false
                active <- active - 1L

                match completion.Response, completion.Failure with
                | Some response, None ->
                    if response <> payload then
                        errors <- errors + 1L
                    else
                        match continuations[mailbox] with
                        | None -> errors <- errors + 1L
                        | Some continuation ->
                            let responseText = Encoding.ASCII.GetString response
                            let resumedState = resume states[mailbox] continuation responseText
                            states[mailbox] <- resumedState
                            continuations[mailbox] <- None
                            let ownerFinishedAt = Stopwatch.GetTimestamp()
                            completionCounts[mailbox] <- completionCounts[mailbox] + 1L
                            completed <- completed + 1L
                            if ownerFinishedAt <= windowDeadline then
                                completedWithinWindow <- completedWithinWindow + 1L
                            if measured then
                                latencies.Add(microsecondsFromTicks (ownerFinishedAt - completion.ScheduledAt))
                | _, Some _ when completion.WasCancelled ->
                    timedOut <- timedOut + 1L
                    continuations[mailbox] <- None
                | _, Some _ ->
                    errors <- errors + 1L
                    continuations[mailbox] <- None
                | _ ->
                    errors <- errors + 1L
                    continuations[mailbox] <- None

        let drainAvailable () =
            task {
                let mutable completion = Unchecked.defaultof<Completion>
                while completions.Reader.TryRead(&completion) do
                    processCompletion completion
            }

        let startRequest mailbox scheduledAt =
            let dispatchAt = Stopwatch.GetTimestamp()
            if measured then
                let dispatchLateness = microsecondsFromTicks (dispatchAt - scheduledAt)
                maxDispatchLatenessMicroseconds <- max maxDispatchLatenessMicroseconds dispatchLateness

            offered <- offered + 1L
            if inFlight[mailbox] then
                rejected <- rejected + 1L
            else
                let nextState, continuation = beginRequest states[mailbox] (Encoding.ASCII.GetString payload)
                states[mailbox] <- nextState
                continuations[mailbox] <- Some continuation
                admissionCounts[mailbox] <- admissionCounts[mailbox] + 1L
                admitted <- admitted + 1L
                inFlight[mailbox] <- true
                active <- active + 1L
                peakPending <- max peakPending active

                let worker =
                    task {
                        let! completion =
                            task {
                                try
                                    let! response = exchange sockets[mailbox] delayMs payload ioCancellation.Token
                                    return
                                        { Mailbox = mailbox
                                          ScheduledAt = scheduledAt
                                          Response = Some response
                                          Failure = None
                                          WasCancelled = false }
                                with error ->
                                    return
                                        { Mailbox = mailbox
                                          ScheduledAt = scheduledAt
                                          Response = None
                                          Failure = Some error.Message
                                          WasCancelled = error :? OperationCanceledException }
                            }
                        do! completions.Writer.WriteAsync completion
                    }
                worker |> ignore

        let waitForCompletionOrDelay (delayMs: int) =
            task {
                use waitCancellation = new CancellationTokenSource()
                let waitForCompletion = completions.Reader.WaitToReadAsync(waitCancellation.Token).AsTask()
                let waitForTime = Task.Delay(delayMs, waitCancellation.Token)
                let! first = Task.WhenAny(waitForCompletion, waitForTime)
                waitCancellation.Cancel()
                if Object.ReferenceEquals(first, waitForCompletion) then
                    try
                        let! _ = waitForCompletion
                        ()
                    with :? OperationCanceledException -> ()
                else
                    try
                        let! _ = waitForCompletion
                        ()
                    with :? OperationCanceledException -> ()
                try
                    do! waitForTime
                with :? OperationCanceledException -> ()
            }

        while sequence < scheduledCount do
            let nextArrival = phaseStart + ticksForArrival sequence rate
            let now = Stopwatch.GetTimestamp()

            if now >= windowDeadline then
                let remaining = scheduledCount - sequence
                offered <- offered + remaining
                rejected <- rejected + remaining
                missedArrivals <- missedArrivals + remaining
                sequence <- scheduledCount
            elif nextArrival <= now then
                let mutable batch = 0
                while batch < catchUpBatchSize && sequence < scheduledCount && Stopwatch.GetTimestamp() < windowDeadline do
                    let arrival = phaseStart + ticksForArrival sequence rate
                    if arrival <= Stopwatch.GetTimestamp() then
                        let mailbox = int (sequence % int64 mailboxCount)
                        startRequest mailbox arrival
                        sequence <- sequence + 1L
                        batch <- batch + 1
                    else
                        batch <- catchUpBatchSize

                do! drainAvailable ()
                if sequence < scheduledCount && Stopwatch.GetTimestamp() < windowDeadline then
                    do! Task.Yield()
            else
                let ticksUntilArrival = max 1L (nextArrival - now)
                let waitMs = max 1 (int (Math.Ceiling(float ticksUntilArrival * 1000.0 / float Stopwatch.Frequency)))
                do! waitForCompletionOrDelay waitMs
                do! drainAvailable ()

        do! drainAvailable ()

        let drainDeadline = Stopwatch.GetTimestamp() + ticksForMilliseconds drainDeadlineMs
        while active > 0L && Stopwatch.GetTimestamp() < drainDeadline do
            let remainingTicks = drainDeadline - Stopwatch.GetTimestamp()
            let remainingMs = max 1 (int (Math.Ceiling(float remainingTicks * 1000.0 / float Stopwatch.Frequency)))
            do! waitForCompletionOrDelay remainingMs
            do! drainAvailable ()

        if active > 0L then
            ioCancellation.Cancel()
            do! drainAvailable ()

            for mailbox in 0 .. mailboxCount - 1 do
                if inFlight[mailbox] then
                    inFlight[mailbox] <- false
                    continuations[mailbox] <- None
                    active <- active - 1L
                    timedOut <- timedOut + 1L

        return
            { Offered = offered
              Admitted = admitted
              Rejected = rejected
              MissedArrivals = missedArrivals
              Completed = completed
              CompletedWithinWindow = completedWithinWindow
              Errors = errors
              TimedOut = timedOut
              PendingAtEnd = active
              PeakPending = peakPending
              MaxDispatchLatenessMicroseconds = maxDispatchLatenessMicroseconds
              LatencyMicroseconds = latencies.ToArray()
              AdmissionCounts = admissionCounts
              CompletionCounts = completionCounts
              States = states }
    }

let private writeResult path (result: PhaseResult) (options: Options) warmupCompleted =
    let payloadText =
        Array.init options.PayloadBytes (fun index -> byte (int 'a' + (index % 26)))
        |> Encoding.ASCII.GetString
    let expectedCompletedLatest = payloadText + payloadText
    let mutable invariantErrors = 0L

    if result.Offered <> result.Admitted + result.Rejected then invariantErrors <- invariantErrors + 1L
    if result.Admitted <> result.Completed + result.Errors + result.TimedOut then invariantErrors <- invariantErrors + 1L
    if int64 result.LatencyMicroseconds.Length <> result.Completed then invariantErrors <- invariantErrors + 1L
    if result.PendingAtEnd <> 0L then invariantErrors <- invariantErrors + 1L

    let mailboxes =
        Array.init mailboxCount (fun id ->
            let state = result.States[id]
            let expectedLatest = if result.CompletionCounts[id] = 0L then String.Empty else expectedCompletedLatest
            let latestVerified =
                state.Attempted = result.AdmissionCounts[id]
                && state.Completed = result.CompletionCounts[id]
                && state.Latest = expectedLatest
            if not latestVerified then invariantErrors <- invariantErrors + 1L

            {| id = id
               attempted = state.Attempted
               completed = state.Completed
               latestVerified = latestVerified |})

    let errors = result.Errors + invariantErrors
    let verified = errors = 0L && result.TimedOut = 0L && result.PendingAtEnd = 0L
    let summary =
        {| schemaVersion = 1
           backend = "fsharp"
           policy = "gc"
           rate = options.Rate
           payloadBytes = options.PayloadBytes
           delayMs = options.DelayMs
           warmupMs = options.WarmupMs
           durationMs = options.DurationMs
           warmupCompleted = warmupCompleted
           offered = result.Offered
           admitted = result.Admitted
           rejected = result.Rejected
           completed = result.Completed
           completedWithinWindow = result.CompletedWithinWindow
           errors = errors
           timedOut = result.TimedOut
           pendingAtEnd = result.PendingAtEnd
           peakPending = result.PeakPending
           verified = verified
           missedArrivals = result.MissedArrivals
           maxDispatchLatenessMicroseconds = result.MaxDispatchLatenessMicroseconds
           latencyMicroseconds = result.LatencyMicroseconds
           mailboxes = mailboxes
           storageReservedBytes = (null: obj) |}

    let outputPath = Path.GetFullPath path
    let directory = Path.GetDirectoryName outputPath
    if not (String.IsNullOrEmpty directory) then Directory.CreateDirectory directory |> ignore
    let temporaryPath = outputPath + ".tmp-" + Guid.NewGuid().ToString("N")
    try
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(summary))
        File.Move(temporaryPath, outputPath, true)
    finally
        if File.Exists temporaryPath then File.Delete temporaryPath

    errors = 0L && result.TimedOut = 0L && result.PendingAtEnd = 0L

let private run (options: Options) =
    task {
        let payload = Array.init options.PayloadBytes (fun index -> byte (int 'a' + (index % 26)))
        let! sockets = connectSockets options.Port
        try
            let! warmupResult =
                if options.WarmupMs = 0 then
                    Task.FromResult(
                        { Offered = 0L
                          Admitted = 0L
                          Rejected = 0L
                          MissedArrivals = 0L
                          Completed = 0L
                          CompletedWithinWindow = 0L
                          Errors = 0L
                          TimedOut = 0L
                          PendingAtEnd = 0L
                          PeakPending = 0L
                          MaxDispatchLatenessMicroseconds = 0L
                          LatencyMicroseconds = Array.empty
                          AdmissionCounts = Array.zeroCreate mailboxCount
                          CompletionCounts = Array.zeroCreate mailboxCount
                          States = Array.init mailboxCount (fun _ -> initialize String.Empty) })
                else
                    runPhase options.Rate options.WarmupMs options.DelayMs payload sockets false

            let payloadText = Encoding.ASCII.GetString payload
            let warmupStateVerified =
                Array.init mailboxCount (fun mailbox ->
                    let state = warmupResult.States[mailbox]
                    let expectedLatest =
                        if warmupResult.CompletionCounts[mailbox] = 0L then String.Empty
                        else payloadText + payloadText
                    state.Attempted = warmupResult.AdmissionCounts[mailbox]
                    && state.Completed = warmupResult.CompletionCounts[mailbox]
                    && state.Latest = expectedLatest)
                |> Array.forall id

            if warmupResult.Offered <> warmupResult.Admitted + warmupResult.Rejected
               || warmupResult.Admitted <> warmupResult.Completed + warmupResult.Errors + warmupResult.TimedOut
               || warmupResult.Errors <> 0L
               || warmupResult.TimedOut <> 0L
               || warmupResult.PendingAtEnd <> 0L
               || not warmupStateVerified then
                invalidOp "Warmup had protocol, state, I/O or timeout failures; measurement was not started."

            let! measuredResult = runPhase options.Rate options.DurationMs options.DelayMs payload sockets true
            let success = writeResult options.Output measuredResult options warmupResult.Completed
            return if success then 0 else 1
        finally
            for socket in sockets do socket.Dispose()
    }

[<EntryPoint>]
let main arguments =
    try
        let options = parseOptions arguments
        run options |> fun task -> task.GetAwaiter().GetResult()
    with error ->
        Console.Error.WriteLine(error.Message)
        Console.Error.WriteLine usage
        1
