module AgentLang.RealIoMailbox.Provider.Program

open System
open System.Collections.Generic
open System.Globalization
open System.IO
open System.Net
open System.Net.Sockets
open System.Text.Json
open System.Threading
open System.Threading.Tasks

type private Options =
    { Port: int
      FragmentBytes: int
      ReadyFile: string
      StopFile: string }

type private Counters() =
    let mutable accepted = 0L
    let mutable completed = 0L
    let mutable rejectedBusy = 0L
    let mutable invalidFrames = 0L
    let mutable timedOut = 0L
    let mutable cancelled = 0L
    let mutable ioFailures = 0L
    let mutable internalErrors = 0L

    member _.Accepted = Interlocked.Read(&accepted)
    member _.Completed = Interlocked.Read(&completed)
    member _.RejectedBusy = Interlocked.Read(&rejectedBusy)
    member _.InvalidFrames = Interlocked.Read(&invalidFrames)
    member _.TimedOut = Interlocked.Read(&timedOut)
    member _.Cancelled = Interlocked.Read(&cancelled)
    member _.IoFailures = Interlocked.Read(&ioFailures)
    member _.InternalErrors = Interlocked.Read(&internalErrors)

    member _.RecordAccepted() = Interlocked.Increment(&accepted) |> ignore
    member _.RecordCompleted() = Interlocked.Increment(&completed) |> ignore
    member _.RecordRejectedBusy() = Interlocked.Increment(&rejectedBusy) |> ignore
    member _.RecordInvalidFrame() = Interlocked.Increment(&invalidFrames) |> ignore
    member _.RecordTimedOut() = Interlocked.Increment(&timedOut) |> ignore
    member _.RecordCancelled() = Interlocked.Increment(&cancelled) |> ignore
    member _.RecordIoFailure() = Interlocked.Increment(&ioFailures) |> ignore
    member _.RecordInternalError() = Interlocked.Increment(&internalErrors) |> ignore

let private maximumDelayMs = 1000u
let private maximumPayloadBytes = 4096u
let private maximumLiveHandlers = 16
let private connectionTimeoutMs = 5000
let private stopPollMs = 50

let private usage =
    "Usage: AgentLang.RealIoMailbox.Provider [--port <0..65535>] [--fragment-bytes <1..4096>] --ready-file <absolute path> --stop-file <absolute path> (defaults: port 0, fragment bytes 4096)"

let private parseOptions (arguments: string array) =
    let mutable port = 0
    let mutable portSeen = false
    let mutable fragmentBytes = int maximumPayloadBytes
    let mutable fragmentBytesSeen = false
    let mutable readyFile: string option = None
    let mutable stopFile: string option = None
    let mutable index = 0

    while index < arguments.Length do
        if index + 1 >= arguments.Length then
            invalidArg "arguments" $"Missing value for {arguments[index]}."

        let value = arguments[index + 1]

        match arguments[index] with
        | "--port" ->
            if portSeen then invalidArg "arguments" "--port may be supplied only once."
            let mutable parsedPort = 0
            if not (Int32.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, &parsedPort)) then
                invalidArg "arguments" "--port must be an integer from 0 through 65535."
            if parsedPort < 0 || parsedPort > 65535 then
                invalidArg "arguments" "--port must be an integer from 0 through 65535."
            port <- parsedPort
            portSeen <- true
        | "--fragment-bytes" ->
            if fragmentBytesSeen then invalidArg "arguments" "--fragment-bytes may be supplied only once."
            let mutable parsedFragmentBytes = 0
            if not (Int32.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, &parsedFragmentBytes)) then
                invalidArg "arguments" "--fragment-bytes must be an integer from 1 through 4096."
            if parsedFragmentBytes < 1 || parsedFragmentBytes > int maximumPayloadBytes then
                invalidArg "arguments" "--fragment-bytes must be an integer from 1 through 4096."
            fragmentBytes <- parsedFragmentBytes
            fragmentBytesSeen <- true
        | "--ready-file" ->
            if readyFile.IsSome then invalidArg "arguments" "--ready-file may be supplied only once."
            readyFile <- Some value
        | "--stop-file" ->
            if stopFile.IsSome then invalidArg "arguments" "--stop-file may be supplied only once."
            stopFile <- Some value
        | option -> invalidArg "arguments" $"Unknown option '{option}'."

        index <- index + 2

    let requireAbsolute (optionName: string) (path: string option) =
        match path with
        | Some value when Path.IsPathFullyQualified value -> Path.GetFullPath value
        | Some _ -> invalidArg "arguments" $"{optionName} must be an absolute path."
        | None -> invalidArg "arguments" $"{optionName} is required."

    let readyPath = requireAbsolute "--ready-file" readyFile
    let stopPath = requireAbsolute "--stop-file" stopFile

    if String.Equals(readyPath, stopPath, StringComparison.OrdinalIgnoreCase) then
        invalidArg "arguments" "--ready-file and --stop-file must name different files."

    { Port = port
      FragmentBytes = fragmentBytes
      ReadyFile = readyPath
      StopFile = stopPath }

let private writeReadyFile (path: string) (port: int) (fragmentBytes: int) =
    let directory = Path.GetDirectoryName path
    if not (String.IsNullOrEmpty directory) then Directory.CreateDirectory directory |> ignore

    let ready =
        {| address = IPAddress.Loopback.ToString()
           port = port
           protocol =
             {| requestHeaderBytes = 8
                responseHeaderBytes = 4
                maximumDelayMs = maximumDelayMs
                maximumPayloadBytes = maximumPayloadBytes
                responseFragmentBytes = fragmentBytes
                maximumLiveAcceptedHandlers = maximumLiveHandlers
                connectionTimeoutMs = connectionTimeoutMs |} |}

    let temporaryPath = path + ".tmp-" + Guid.NewGuid().ToString("N")
    try
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize ready)
        File.Move(temporaryPath, path, true)
    finally
        if File.Exists temporaryPath then File.Delete temporaryPath

let private receiveExactly (socket: Socket) (buffer: byte array) cancellationToken =
    task {
        let mutable offset = 0

        while offset < buffer.Length do
            let! received = socket.ReceiveAsync(buffer.AsMemory(offset), SocketFlags.None, cancellationToken)
            if received = 0 then raise (EndOfStreamException("The peer closed before the complete frame arrived."))
            offset <- offset + received
    }

let private sendInChunks (socket: Socket) (buffer: byte array) (fragmentBytes: int) (delayAfterFirstChunk: bool) cancellationToken =
    task {
        let mutable offset = 0
        let mutable firstChunk = true

        while offset < buffer.Length do
            let chunkEnd = min buffer.Length (offset + fragmentBytes)

            while offset < chunkEnd do
                let! sent = socket.SendAsync(buffer.AsMemory(offset, chunkEnd - offset), SocketFlags.None, cancellationToken)
                if sent = 0 then raise (IOException("The socket accepted no response bytes."))
                offset <- offset + sent

            if delayAfterFirstChunk && firstChunk && offset < buffer.Length then
                do! Task.Delay(1, cancellationToken)

            firstChunk <- false
    }

let private readUInt32LittleEndian (buffer: byte array) offset =
    uint32 buffer[offset]
    ||| (uint32 buffer[offset + 1] <<< 8)
    ||| (uint32 buffer[offset + 2] <<< 16)
    ||| (uint32 buffer[offset + 3] <<< 24)

let private writeUInt32LittleEndian value =
    [| byte (value &&& 0xFFu)
       byte ((value >>> 8) &&& 0xFFu)
       byte ((value >>> 16) &&& 0xFFu)
       byte ((value >>> 24) &&& 0xFFu) |]

let private serveClient (client: TcpClient) (fragmentBytes: int) (shutdownToken: CancellationToken) (counters: Counters) =
    task {
        use clientLifetime = client

        try
            use connectionCancellation = CancellationTokenSource.CreateLinkedTokenSource shutdownToken
            connectionCancellation.CancelAfter connectionTimeoutMs
            let token = connectionCancellation.Token
            let socket = clientLifetime.Client
            socket.NoDelay <- true

            let requestHeader = Array.zeroCreate<byte> 8
            do! receiveExactly socket requestHeader token

            let delayMs = readUInt32LittleEndian requestHeader 0
            let payloadByteCount = readUInt32LittleEndian requestHeader 4

            if delayMs > maximumDelayMs || payloadByteCount > maximumPayloadBytes then
                counters.RecordInvalidFrame()
            else
                let payload = Array.zeroCreate<byte> (int payloadByteCount)
                do! receiveExactly socket payload token
                do! Task.Delay(int delayMs, token)
                do! sendInChunks socket (writeUInt32LittleEndian payloadByteCount) fragmentBytes true token
                do! sendInChunks socket payload fragmentBytes false token
                counters.RecordCompleted()
        with
        | :? OperationCanceledException ->
            if shutdownToken.IsCancellationRequested then counters.RecordCancelled()
            else counters.RecordTimedOut()
        | :? SocketException
        | :? IOException
        | :? ObjectDisposedException -> counters.RecordIoFailure()
        | error ->
            counters.RecordInternalError()
            Console.Error.WriteLine($"provider: handler error {error.GetType().Name}")
    }

let private watchStopFile (path: string) (shutdown: CancellationTokenSource) =
    task {
        try
            while not shutdown.IsCancellationRequested do
                if File.Exists path then
                    shutdown.Cancel()
                else
                    do! Task.Delay(stopPollMs, shutdown.Token)
        with
        | :? OperationCanceledException -> ()
        | error ->
            Console.Error.WriteLine($"provider: stop-file watcher error {error.GetType().Name}")
            shutdown.Cancel()
    }

let private reapCompleted (handlers: ResizeArray<Task>) =
    task {
        let completed = handlers |> Seq.filter (fun handler -> handler.IsCompleted) |> Seq.toArray
        for handler in completed do
            do! handler
            handlers.Remove handler |> ignore
    }

let private printCounters (counters: Counters) =
    let classifiedOutcomes =
        counters.Completed
        + counters.RejectedBusy
        + counters.InvalidFrames
        + counters.TimedOut
        + counters.Cancelled
        + counters.IoFailures
        + counters.InternalErrors

    if counters.Accepted <> classifiedOutcomes then
        invalidOp $"Provider outcome accounting mismatch: accepted={counters.Accepted}, classified={classifiedOutcomes}."

    let summary =
        {| accepted = counters.Accepted
           completed = counters.Completed
           rejectedBusy = counters.RejectedBusy
           invalidFrames = counters.InvalidFrames
           timedOut = counters.TimedOut
           cancelled = counters.Cancelled
           ioFailures = counters.IoFailures
           internalErrors = counters.InternalErrors |}

    Console.WriteLine(JsonSerializer.Serialize summary)

let private runServer options =
    task {
        use shutdown = new CancellationTokenSource()
        use listener = new TcpListener(IPAddress.Loopback, options.Port)
        let counters = Counters()
        let handlers = ResizeArray<Task>()
        let cancelOnConsole =
            ConsoleCancelEventHandler(fun _ eventArgs ->
                eventArgs.Cancel <- true
                shutdown.Cancel())

        listener.Start(maximumLiveHandlers)
        let endpoint = listener.LocalEndpoint :?> IPEndPoint
        writeReadyFile options.ReadyFile endpoint.Port options.FragmentBytes
        let stopWatcher = watchStopFile options.StopFile shutdown
        Console.CancelKeyPress.AddHandler cancelOnConsole

        let mutable serverFailure: exn option = None

        try
            while not shutdown.IsCancellationRequested do
                do! reapCompleted handlers

                if not shutdown.IsCancellationRequested then
                    let! client = listener.AcceptTcpClientAsync(shutdown.Token)
                    counters.RecordAccepted()
                    do! reapCompleted handlers

                    if handlers.Count >= maximumLiveHandlers then
                        counters.RecordRejectedBusy()
                        client.Dispose()
                    else
                        handlers.Add(serveClient client options.FragmentBytes shutdown.Token counters :> Task)
        with
        | :? OperationCanceledException when shutdown.IsCancellationRequested -> ()
        | :? SocketException when shutdown.IsCancellationRequested -> ()
        | :? ObjectDisposedException when shutdown.IsCancellationRequested -> ()
        | error ->
            serverFailure <- Some error
            shutdown.Cancel()

        shutdown.Cancel()
        listener.Stop()
        Console.CancelKeyPress.RemoveHandler cancelOnConsole
        do! stopWatcher
        do! reapCompleted handlers
        do! Task.WhenAll(handlers.ToArray())
        printCounters counters

        match serverFailure with
        | Some error -> return raise error
        | None -> return ()
    }

[<EntryPoint>]
let main arguments =
    try
        let options = parseOptions arguments
        runServer options |> fun serverTask -> serverTask.GetAwaiter().GetResult()
        0
    with error ->
        Console.Error.WriteLine(error.Message)
        Console.Error.WriteLine usage
        1
