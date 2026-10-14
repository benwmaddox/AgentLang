module AgentLang.FlowHttp.Tests

// Portable Flow HTTP host and db API (docs/LINUX-SERVER-PLAN.md A and C):
// 1. the counter app runs in the interpreter (db host operations through
//    Microsoft.Data.Sqlite's SQLite) for a fixed request sequence;
// 2. the same app is built natively for Windows and for Linux (WSL) with
//    scripts/Build-FlowHttpServer.ps1, served on 127.0.0.1:13773, and the
//    same sequence over one keep-alive connection must give the same status,
//    app headers and body (interpreter-vs-native parity, including db rows,
//    change counts and DbError codes);
// 3. protocol checks: pipelining, HEAD, 404, malformed requests, chunked
//    bodies, Connection: close, oversized bodies, control shutdown.

open System
open System.Diagnostics
open System.IO
open System.Net.Sockets
open System.Text
open Microsoft.Data.Sqlite
open AgentLang
open AgentLang.FlowHttp

let private repo = Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, "../.."))
let private appSource = Path.Combine(__SOURCE_DIRECTORY__, "app", "counter.flow")
let private port = 13773
let private assertions = ref 0

let private check label condition =
    assertions.Value <- assertions.Value + 1
    if not condition then failwith $"Assertion failed: {label}"

let private config = [ "greeting", "world"; "secret", "s3cret" ]

let private createDatabase (path: string) =
    for suffix in [ ""; "-wal"; "-shm" ] do
        if File.Exists(path + suffix) then File.Delete(path + suffix)
    use connection = new SqliteConnection($"Data Source={path};Pooling=False")
    connection.Open()
    use command = connection.CreateCommand()
    command.CommandText <- "create table items(id integer primary key, name text not null unique, note text); insert into items(name) values ('first');"
    command.ExecuteNonQuery() |> ignore

type private Case = { Method: string; Target: string; Headers: (string * string) list; Body: string }

let private case method target (extra: (string * string) list) (body: string) =
    let length = if body = "" && method = "GET" then [] else [ "Content-Length", string (Encoding.UTF8.GetByteCount body) ]
    { Method = method; Target = target; Headers = ("Host", "127.0.0.1") :: extra @ length; Body = body }

let private cases =
    [ case "GET" "/hello" [] ""
      case "GET" "/context" [] ""
      case "GET" "/items" [] ""
      case "POST" "/items" [ "X-Note", "n2" ] "second"
      case "POST" "/items" [] "third"
      case "POST" "/items" [] "first"                     // UNIQUE violation: DbError 2067
      case "GET" "/items" [] ""
      case "GET" "/item?id=2" [] ""
      case "GET" "/item?id=3" [] ""
      case "GET" "/item?id=99" [] ""
      case "POST" "/echo?a=1&b=2" [ "X-Probe", "pp"; "Accept", "*/*" ] "hello body"
      case "GET" "/bad-sql" [] ""
      case "GET" "/nope" [] "" ]

type private Response = { Status: int; Headers: (string * string) list; Body: string }

let private interpreterResponses () =
    let root = Path.Combine(repo, ".agentlang", "flowhttp-tests", "interp-" + Guid.NewGuid().ToString("N").Substring(0, 8))
    Directory.CreateDirectory root |> ignore
    let database = Path.Combine(root, "app.db")
    createDatabase database
    use app = FlowApp.load (Path.Combine(root, "engine")) "counter" [ appSource ] database
    let context = FlowApp.workerInit app config
    cases
    |> List.map (fun c ->
        let request = FlowApp.request c.Method c.Target c.Headers (Encoding.UTF8.GetBytes c.Body) "127.0.0.1" 0L
        match FlowApp.handle app request context with
        | RecordValue("HttpResponse", fields) ->
            let headers =
                match fields["headers"] with
                | ListValue(_, items) ->
                    items |> List.map (function
                        | RecordValue(_, h) -> (match h["name"], h["value"] with StringValue n, StringValue v -> n, v | _ -> failwith "header")
                        | _ -> failwith "header")
                | _ -> failwith "headers"
            match fields["status"], fields["body"] with
            | IntValue status, StringValue body -> { Status = int status; Headers = headers; Body = body }
            | _ -> failwith "response"
        | other -> failwith $"unexpected response {other}")

// ---- raw HTTP/1.1 client ----

let private encodeRequest (c: Case) =
    let builder = StringBuilder()
    builder.Append($"{c.Method} {c.Target} HTTP/1.1\r\n") |> ignore
    for name, value in c.Headers do builder.Append($"{name}: {value}\r\n") |> ignore
    builder.Append("\r\n").Append(c.Body) |> ignore
    Encoding.UTF8.GetBytes(builder.ToString())

type private Client(stream: NetworkStream) =
    let buffer = ResizeArray<byte>()
    let chunk = Array.zeroCreate<byte> 65536
    let fill () =
        let n = stream.Read(chunk, 0, chunk.Length)
        if n = 0 then false else (buffer.AddRange(chunk.AsSpan(0, n).ToArray()); true)
    member _.Send(bytes: byte[]) = stream.Write(bytes, 0, bytes.Length)
    /// Reads one response; None when the server closed the connection first.
    member _.Read(headOnly: bool) : Response option =
        let rec headEnd () =
            let text = Encoding.Latin1.GetString(buffer.ToArray())
            match text.IndexOf("\r\n\r\n") with
            | -1 -> if fill () then headEnd () else -1
            | index -> index
        match headEnd () with
        | -1 -> None
        | index ->
            let head = Encoding.Latin1.GetString(buffer.GetRange(0, index).ToArray()).Split("\r\n")
            let status = int (head[0].Split(' ')[1])
            let headers = head |> Array.skip 1 |> Array.map (fun line -> let i = line.IndexOf(':') in line.Substring(0, i).ToLowerInvariant(), line.Substring(i + 1).Trim()) |> List.ofArray
            let length = headers |> List.tryFind (fun (n, _) -> n = "content-length") |> Option.map (snd >> int) |> Option.defaultValue 0
            let bodyLength = if headOnly then 0 else length
            while buffer.Count < index + 4 + bodyLength && fill () do ()
            let body = Encoding.UTF8.GetString(buffer.GetRange(index + 4, bodyLength).ToArray())
            buffer.RemoveRange(0, index + 4 + bodyLength)
            Some { Status = status; Headers = headers; Body = body }
    member _.Closed() = buffer.Count = 0 && not (fill ())

let private connect () =
    let tcp = new TcpClient()
    tcp.Connect("127.0.0.1", port)
    tcp.ReceiveTimeout <- 10000
    tcp, Client(tcp.GetStream())

let private appHeaders (response: Response) =
    response.Headers |> List.filter (fun (n, _) -> n <> "content-length" && n <> "connection")

// ---- server processes ----

type private Server = { Process: Process; Output: StringBuilder; Errors: StringBuilder; Label: string }

let private startServer label (fileName: string) (arguments: string) =
    let info = ProcessStartInfo(fileName, arguments, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false)
    let proc = new Process(StartInfo = info)
    let output = StringBuilder()
    let errors = StringBuilder()
    let ready = new Threading.ManualResetEventSlim(false)
    proc.OutputDataReceived.Add(fun e -> if not (isNull e.Data) then (lock output (fun () -> output.AppendLine e.Data |> ignore); if e.Data.StartsWith "READY" then ready.Set()))
    proc.ErrorDataReceived.Add(fun e -> if not (isNull e.Data) then lock errors (fun () -> errors.AppendLine e.Data |> ignore))
    proc.Start() |> ignore
    proc.BeginOutputReadLine()
    proc.BeginErrorReadLine()
    if not (ready.Wait(TimeSpan.FromSeconds 30.0)) then
        (try proc.Kill(true) with _ -> ())
        failwith $"{label} server did not print READY: {errors}"
    check $"{label}: READY {port}" (output.ToString().Contains($"READY {port}"))
    { Process = proc; Output = output; Errors = errors; Label = label }

let private stopServer (server: Server) =
    let tcp, client = connect ()
    client.Send(encodeRequest (case "POST" "/__flow_http/shutdown" [] ""))
    let response = client.Read(false)
    check $"{server.Label}: control shutdown answered" (response |> Option.exists (fun r -> r.Status = 200))
    tcp.Dispose()
    check $"{server.Label}: exits after control shutdown" (server.Process.WaitForExit(15000))
    server.Process.WaitForExit()
    let errors = lock server.Errors (fun () -> server.Errors.ToString())
    check $"{server.Label}: exit code 0 ({server.Process.ExitCode}) {errors}" (server.Process.ExitCode = 0)
    check $"{server.Label}: clean stop with no failed requests: {errors}" (errors.Contains("STOPPED") && errors.Contains("failures=0"))
    check $"{server.Label}: config values are never logged" (not (errors.Contains "s3cret") && not (server.Output.ToString().Contains "s3cret"))

// ---- checks against a running server ----

let private parity label (expected: Response list) =
    let tcp, client = connect ()
    use _tcp = tcp
    for c, want in List.zip cases expected do
        client.Send(encodeRequest c)
        match client.Read(false) with
        | None -> failwith $"{label}: connection closed during {c.Method} {c.Target}"
        | Some got ->
            check $"{label} {c.Method} {c.Target}: status {got.Status} = {want.Status}" (got.Status = want.Status)
            check $"{label} {c.Method} {c.Target}: body {got.Body} = {want.Body}" (got.Body = want.Body)
            check $"{label} {c.Method} {c.Target}: headers {appHeaders got} = {want.Headers}" (appHeaders got = want.Headers)
            check $"{label} {c.Method} {c.Target}: content-length" (got.Headers |> List.contains ("content-length", string (Encoding.UTF8.GetByteCount got.Body)))

let private protocol label =
    // Pipelining: two requests in one write, answered in order.
    (let tcp, client = connect ()
     use _tcp = tcp
     client.Send(Array.append (encodeRequest (case "GET" "/hello" [] "")) (encodeRequest (case "GET" "/missing" [] "")))
     let first = client.Read(false)
     let second = client.Read(false)
     check $"{label}: pipelined first" (first |> Option.exists (fun r -> r.Status = 200 && r.Body = "hello world"))
     check $"{label}: pipelined second is 404" (second |> Option.exists (fun r -> r.Status = 404 && r.Body = "not found: /missing"))
     // HEAD: headers with the GET content length, no body.
     client.Send(encodeRequest (case "HEAD" "/hello" [] ""))
     let head = client.Read(true)
     check $"{label}: HEAD keeps content-length" (head |> Option.exists (fun r -> r.Status = 200 && r.Headers |> List.contains ("content-length", "11")))
     client.Send(encodeRequest (case "GET" "/hello" [] ""))
     check $"{label}: keep-alive after HEAD" (client.Read(false) |> Option.exists (fun r -> r.Body = "hello world"))
     // Connection: close is honored.
     client.Send(encodeRequest (case "GET" "/hello" [ "Connection", "close" ] ""))
     let closing = client.Read(false)
     check $"{label}: close response" (closing |> Option.exists (fun r -> r.Headers |> List.contains ("connection", "close")))
     check $"{label}: connection closed after Connection: close" (client.Closed()))
    let expectError (raw: string) status =
        let tcp, client = connect ()
        use _tcp = tcp
        client.Send(Encoding.ASCII.GetBytes raw)
        let response = client.Read(false)
        check $"{label}: {raw.Split('\r')[0]} -> {status}" (response |> Option.exists (fun r -> r.Status = status))
        check $"{label}: error closes the connection" (client.Closed())
    expectError "NONSENSE\r\n\r\n" 400
    expectError "POST /items HTTP/1.1\r\nHost: x\r\nTransfer-Encoding: chunked\r\n\r\n5\r\nhello\r\n0\r\n\r\n" 501
    expectError "POST /items HTTP/1.1\r\nHost: x\r\nContent-Length: 999999999\r\n\r\n" 413
    expectError "GET /hello HTTP/1.1\r\nContent-Length: x\r\n\r\n" 400
    // Several connections at once (spread over workers): every worker's context answers.
    let clients = [ for _ in 1 .. 8 -> connect () ]
    for _, client in clients do client.Send(encodeRequest (case "GET" "/context" [] ""))
    let contexts = clients |> List.map (fun (_, client) -> client.Read(false) |> Option.map (fun r -> r.Body))
    for tcp, _ in clients do tcp.Dispose()
    check $"{label}: worker contexts agree across connections {contexts}" (contexts |> List.forall (fun body -> body = contexts.Head) && contexts.Head.IsSome)

// ---- builds ----

let private powershell (arguments: string) =
    let info = ProcessStartInfo("powershell.exe", "-NoProfile -ExecutionPolicy Bypass " + arguments, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, WorkingDirectory = repo)
    use proc = Process.Start info
    let output = proc.StandardOutput.ReadToEndAsync()
    let errors = proc.StandardError.ReadToEnd()
    proc.WaitForExit()
    if proc.ExitCode <> 0 then failwith $"powershell {arguments} failed: {output.Result} {errors}"
    output.Result.Trim().Split('\n') |> Array.last |> fun line -> line.Trim()

let private build target outDir profile =
    powershell $"-File scripts/Build-FlowHttpServer.ps1 -App counter -Source \"{appSource}\" -Target {target} -RuntimeProfile {profile} -OutDir \"{outDir}\" -LinuxName counter-test"

let private wslPath (path: string) = "/mnt/" + string (Char.ToLowerInvariant path[0]) + path.Substring(2).Replace('\\', '/')

let private run () =
    let root = Path.Combine(repo, ".agentlang", "flowhttp-tests", DateTimeOffset.UtcNow.ToString("yyyyMMddTHHmmss") + "-" + Guid.NewGuid().ToString("N").Substring(0, 6))
    Directory.CreateDirectory root |> ignore
    let configPath = Path.Combine(root, "config.txt")
    File.WriteAllText(configPath, (config |> List.map (fun (n, v) -> $"{n}={v}") |> String.concat "\n") + "\n")

    // Literal-SQL rule.
    check "non-literal SQL is rejected" (Db.nonLiteralSqlCalls "fn f(s: String) -> Int { db.execute(s, list.empty<DbValue>()) }" |> List.isEmpty |> not)
    check "literal SQL is accepted" (Db.nonLiteralSqlCalls "db.query(\"select 1\", list.empty<DbValue>())" |> List.isEmpty)

    let expected = interpreterResponses ()
    check "interpreter: hello" (expected.Head.Body = "hello world")
    check "interpreter: unique violation maps to DbError 2067" (expected[5].Status = 409 && expected[5].Body = "db error 2067")
    check "interpreter: listing after inserts" (expected[6].Body = "1:first\n2:second\n3:third\n")
    check "interpreter: optional-text null" (expected[8].Body = "third / (none)")
    printfn "interpreter: %d responses" expected.Length

    let profiles =
        match Environment.GetEnvironmentVariable "AGENTLANG_FLOWHTTP_PROFILES" with
        | null | "" -> [ "trusted"; "diagnostic" ]
        | text -> text.Split(',') |> List.ofArray
    for profile in profiles do
        // Windows
        let winDir = Path.Combine(root, "win-" + profile)
        let exe = build "windows-x64" winDir profile
        let database = Path.Combine(root, $"win-{profile}.db")
        createDatabase database
        let server = startServer $"windows/{profile}" exe $"--port {port} --db \"{database}\" --config \"{configPath}\" --workers 3 --allow-control --arena-mb 8"
        try
            parity $"windows/{profile}" expected
            protocol $"windows/{profile}"
        finally stopServer server
        printfn "windows/%s: parity and protocol checks passed" profile

        // Linux (WSL), unless skipped.
        if Environment.GetEnvironmentVariable "AGENTLANG_FLOWHTTP_SKIP_LINUX" <> "1" then
            let linuxDir = Path.Combine(root, "linux-" + profile)
            let binary = build "linux-x64" linuxDir profile
            let linuxDatabase = Path.Combine(root, $"linux-{profile}.db")
            createDatabase linuxDatabase
            let remote = "~/linuxbench/flowhttp/counter-test"
            let command =
                $"cp '{wslPath linuxDatabase}' {remote}/test.db && rm -f {remote}/test.db-wal {remote}/test.db-shm && cp '{wslPath configPath}' {remote}/config.txt && " +
                $"cd {remote} && exec {binary} --port {port} --db test.db --config config.txt --workers 3 --allow-control --arena-mb 8"
            let server = startServer $"linux/{profile}" "wsl.exe" $"-- bash -lc \"{command}\""
            try
                parity $"linux/{profile}" expected
                protocol $"linux/{profile}"
            finally stopServer server
            printfn "linux/%s: parity and protocol checks passed" profile

    printfn "AgentLang.FlowHttp.Tests: %d assertions passed; artifacts %s" assertions.Value root

[<EntryPoint>]
let main _ =
    try
        run ()
        0
    with error ->
        eprintfn "%s" (error.ToString())
        1
