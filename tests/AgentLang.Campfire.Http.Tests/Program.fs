namespace AgentLang.Campfire.Http.Tests

open System
open System.Globalization
open System.IO
open System.Net
open System.Net.Http
open System.Net.Http.Headers
open System.Security.Cryptography
open System.Text
open System.Text.Json
open System.Text.Json.Nodes
open System.Threading
open System.Threading.Tasks
open AgentLang
open AgentLang.Campfire.Browser.Host
open AgentLang.Campfire.Cookies
open AgentLang.Campfire.Host
open AgentLang.Campfire.Authentication.Host
open AgentLang.Campfire.Persistence
open AgentLang.Campfire.Authentication.Persistence
open AgentLang.Campfire.Http
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Hosting
open Microsoft.AspNetCore.Hosting.Server
open Microsoft.AspNetCore.Hosting.Server.Features
open Microsoft.Data.Sqlite
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Logging

[<Sealed>]
type private FixedClock(instant: DateTimeOffset) =
    interface IClock with
        member _.UtcNow() = instant

[<Sealed>]
type private ProviderProbe(provider: HostOperationProvider) =
    let calls = ResizeArray<string * Value list>()
    let mutable disposeCalls = 0

    member _.Calls = calls |> Seq.toList
    member _.Names = calls |> Seq.map fst |> Seq.toList
    member _.Clear() = calls.Clear()
    member _.DisposeCalls = disposeCalls

    member _.AsProvider =
        { OperationIds = provider.OperationIds
          Invoke = fun operation values ->
              let (HostOperationId name) = operation
              calls.Add((name, values))
              provider.Invoke operation values
          Dispose = fun () ->
              disposeCalls <- disposeCalls + 1
              provider.Dispose() }

type private RequestSpec =
    { Method: HttpMethod
      Path: string
      Cookies: (string * string) list
      CookieHeaderOverride: string option
      ContentType: string option
      Body: string option
      Chunked: bool
      Accept: string option
      Origin: string option
      FetchSite: string option
      UserAgent: string option
      ExtraHeaders: (string * string) list }

type private HttpExchange =
    { Status: int
      Body: string
      Location: string option
      ContentType: string option
      SetCookies: string list }

type private DatabaseSnapshot =
    { Messages: int64
      RichTextBodies: int64
      RoomUpdatedAt: string
      FreshSessionLastActive: string
      DueSessionLastActive: string
      OrphanSessionLastActive: string
      FailedRefreshLastActive: string }

[<Sealed>]
type private UnknownLengthContent(body: string) =
    inherit HttpContent()

    let bytes = Encoding.UTF8.GetBytes body

    override _.TryComputeLength(length: byref<int64>) : bool =
        length <- 0L
        false

    override _.SerializeToStreamAsync(stream: Stream, _context: TransportContext) : Task =
        stream.WriteAsync(bytes, 0, bytes.Length)

    override _.SerializeToStreamAsync(stream: Stream, _context: TransportContext, cancellationToken: CancellationToken) : Task =
        stream.WriteAsync(bytes, 0, bytes.Length, cancellationToken)

module Program =
    let mutable private assertions = 0
    let mutable private currentCheck = "startup"
    let private startedAt = DateTimeOffset.UtcNow
    let private repositoryRoot = Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, "..", ".."))
    let private runId =
        DateTimeOffset.UtcNow.ToString("yyyyMMddTHHmmssfffZ", CultureInfo.InvariantCulture)
        + "-" + Guid.NewGuid().ToString("N")
    let private runDirectory = Path.Combine(repositoryRoot, ".agentlang", "campfire-http-242", "http-tests", "runs", runId)
    let private projectDirectory = Path.Combine(runDirectory, "project")
    let private databasePath = Path.Combine(runDirectory, "campfire.sqlite3")
    let private transcriptPath = Path.Combine(runDirectory, "http-transcript.jsonl")
    let private operationTracePath = Path.Combine(runDirectory, "provider-traces.jsonl")
    let private receiptPath = Path.Combine(runDirectory, "receipt.json")
    let private publicUri = Uri("http://campfire.test")
    let private now = DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero)
    let private goodAgent = "Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0.0.0 Safari/537.36"
    let private invalidMarkup = "<script>alert('unsafe')</script>"
    let mutable private failureMessage: string option = None

    let private jsonStringNode (value: string) : JsonNode = JsonValue.Create(value) :> JsonNode

    let private optionalStringNode (value: string option) : JsonNode =
        match value with
        | Some text -> jsonStringNode text
        | None -> null

    let private check condition message =
        assertions <- assertions + 1
        if not condition then failwith message

    let private equal expected actual message =
        assertions <- assertions + 1
        if expected <> actual then failwith $"{message}: expected {expected}, got {actual}"

    let private expectOk label = function
        | Ok value -> value
        | Error error -> failwith $"{label}: {error}"

    let private appendTranscript (request: RequestSpec) (response: HttpExchange) =
        let requestNode = JsonObject()
        requestNode["method"] <- JsonValue.Create(request.Method.Method)
        requestNode["path"] <- JsonValue.Create(request.Path)
        requestNode["cookieNames"] <-
            (if request.CookieHeaderOverride.IsSome then
                 let values = JsonArray()
                 values.Add(JsonValue.Create("<custom-redacted>"))
                 values
             else
                 let values = JsonArray()
                 for name, _ in request.Cookies do values.Add(JsonValue.Create name)
                 values)
        requestNode["contentType"] <- optionalStringNode request.ContentType
        requestNode["chunked"] <- JsonValue.Create request.Chunked
        requestNode["accept"] <- optionalStringNode request.Accept
        requestNode["origin"] <- optionalStringNode request.Origin
        requestNode["fetchSite"] <- optionalStringNode request.FetchSite
        requestNode["body"] <- optionalStringNode request.Body

        let responseNode = JsonObject()
        responseNode["status"] <- JsonValue.Create response.Status
        responseNode["location"] <- optionalStringNode response.Location
        responseNode["contentType"] <- optionalStringNode response.ContentType
        responseNode["body"] <- JsonValue.Create response.Body
        let cookies = JsonArray()
        for header in response.SetCookies do
            cookies.Add(JsonValue.Create(header.Split('=', 2)[0]))
        responseNode["setCookieNames"] <- cookies
        let line = JsonObject()
        line["request"] <- requestNode
        line["response"] <- responseNode
        File.AppendAllText(transcriptPath, line.ToJsonString() + Environment.NewLine, Encoding.UTF8)

    let private openConnection path mode =
        let builder = SqliteConnectionStringBuilder()
        builder.DataSource <- path
        builder.Mode <- mode
        builder.ForeignKeys <- true
        builder.Pooling <- false
        builder.DefaultTimeout <- 5
        let connection = new SqliteConnection(builder.ToString())
        connection.Open()
        connection

    let private execute (connection: SqliteConnection) sql (parameters: (string * obj) list) =
        use command = connection.CreateCommand()
        command.CommandText <- sql
        for name, value in parameters do command.Parameters.AddWithValue(name, value) |> ignore
        command.ExecuteNonQuery()

    let private executeAtPath path sql parameters =
        use connection = openConnection path SqliteOpenMode.ReadWrite
        execute connection sql parameters

    let private nullable value = match value with Some text -> box text | None -> box DBNull.Value

    let private seedFixture (path: string) =
        Directory.CreateDirectory(Path.GetDirectoryName path) |> ignore
        use connection = openConnection path SqliteOpenMode.ReadWriteCreate
        use command = connection.CreateCommand()
        command.CommandText <-
            "CREATE TABLE users (id INTEGER PRIMARY KEY, name TEXT NOT NULL, role INTEGER NOT NULL, status INTEGER NOT NULL, bot_token TEXT, created_at TEXT NOT NULL, updated_at TEXT NOT NULL); " +
            "CREATE UNIQUE INDEX users_bot_token_unique ON users(bot_token); " +
            "CREATE TABLE sessions (id INTEGER PRIMARY KEY, user_id INTEGER NOT NULL REFERENCES users(id), token TEXT NOT NULL UNIQUE, ip_address TEXT, user_agent TEXT, last_active_at TEXT NOT NULL, created_at TEXT NOT NULL, updated_at TEXT NOT NULL); " +
            "CREATE TABLE bans (id INTEGER PRIMARY KEY, user_id INTEGER NOT NULL REFERENCES users(id), ip_address TEXT NOT NULL, created_at TEXT NOT NULL, updated_at TEXT NOT NULL); " +
            "CREATE TABLE rooms (id INTEGER PRIMARY KEY, created_at TEXT NOT NULL, creator_id INTEGER NOT NULL, name TEXT, type TEXT NOT NULL, updated_at TEXT NOT NULL); " +
            "CREATE TABLE memberships (id INTEGER PRIMARY KEY, room_id INTEGER NOT NULL, user_id INTEGER NOT NULL, created_at TEXT NOT NULL, updated_at TEXT NOT NULL); " +
            "CREATE UNIQUE INDEX memberships_room_user_unique ON memberships(room_id, user_id); " +
            "CREATE TABLE messages (id INTEGER PRIMARY KEY AUTOINCREMENT, client_message_id TEXT NOT NULL, created_at TEXT NOT NULL, creator_id INTEGER NOT NULL REFERENCES users(id), room_id INTEGER NOT NULL REFERENCES rooms(id), updated_at TEXT NOT NULL); " +
            "CREATE TABLE action_text_rich_texts (id INTEGER PRIMARY KEY AUTOINCREMENT, body TEXT, created_at TEXT NOT NULL, name TEXT NOT NULL, record_id INTEGER NOT NULL, record_type TEXT NOT NULL, updated_at TEXT NOT NULL); " +
            "CREATE UNIQUE INDEX action_text_rich_texts_unique ON action_text_rich_texts(record_type, record_id, name); " +
            "CREATE INDEX sessions_token_index ON sessions(token); " +
            "CREATE INDEX bans_ip_address_index ON bans(ip_address); " +
            "CREATE TRIGGER reject_blocked_refresh BEFORE UPDATE ON sessions WHEN OLD.token = 'blocked-refresh-token' BEGIN SELECT RAISE(ABORT, 'test-only refresh failure'); END; " +
            "CREATE TRIGGER reject_test_body BEFORE INSERT ON action_text_rich_texts WHEN NEW.body = 'abort-after-insert' BEGIN SELECT RAISE(ABORT, 'test-only message write failure'); END"
        command.ExecuteNonQuery() |> ignore

        for id, name, role, status, botToken in
            [ 1, "member", 0, 0, None
              2, "other-member", 0, 0, None
              3, "administrator", 1, 0, None
              4, "bot", 2, 0, Some "4-bot-secret" ] do
            execute connection
                "INSERT INTO users(id, name, role, status, bot_token, created_at, updated_at) VALUES ($id, $name, $role, $status, $bot, '2025-01-01 00:00:00', '2025-01-01 00:00:00')"
                [ "$id", box id; "$name", box name; "$role", box role; "$status", box status; "$bot", nullable botToken ]
            |> ignore

        let insertSession id userId token ip agent lastActive =
            execute connection
                "INSERT INTO sessions(id, user_id, token, ip_address, user_agent, last_active_at, created_at, updated_at) VALUES ($id, $user, $token, $ip, $agent, $last, '2025-01-01 00:00:00', $last)"
                [ "$id", box id; "$user", box userId; "$token", box token
                  "$ip", nullable ip; "$agent", nullable agent; "$last", box lastActive ]
            |> ignore

        insertSession 10 1 "fresh-token" (Some "198.51.100.10") (Some "stored-agent") "2026-01-01 11:30:00"
        insertSession 11 1 "due-token" (Some "198.51.100.11") (Some "stored-due-agent") "2025-12-31 00:00:00"
        insertSession 12 1 "blocked-refresh-token" (Some "198.51.100.12") (Some "blocked-agent") "2025-12-31 00:00:00"
        insertSession 13 2 "no-access-token" (Some "198.51.100.13") (Some "other-agent") "2026-01-01 11:30:00"
        execute connection "PRAGMA foreign_keys = OFF" [] |> ignore
        insertSession 14 99 "orphan-token" (Some "198.51.100.14") (Some "orphan-agent") "2025-12-31 00:00:00"
        execute connection "PRAGMA foreign_keys = ON" [] |> ignore

        execute connection
            "INSERT INTO rooms(id, created_at, creator_id, name, type, updated_at) VALUES (1, '2025-01-01 00:00:00', 1, 'Main room', 'Rooms::Open', '2025-01-01 00:00:00'), (2, '2025-01-01 00:00:00', 2, 'Private room', 'Rooms::Closed', '2025-01-01 00:00:00')"
            []
        |> ignore
        execute connection
            "INSERT INTO memberships(id, room_id, user_id, created_at, updated_at) VALUES (1, 1, 1, '2025-01-01 00:00:00', '2025-01-01 00:00:00'), (2, 2, 2, '2025-01-01 00:00:00', '2025-01-01 00:00:00')"
            []
        |> ignore

        for id in 1L .. 45L do
            let timestamp = "2026-01-01 00:00:" + id.ToString("D2", CultureInfo.InvariantCulture)
            let body = if id = 45L then invalidMarkup else $"message body {id}"
            execute connection
                "INSERT INTO messages(id, client_message_id, created_at, creator_id, room_id, updated_at) VALUES ($id, $client, $created, 1, 1, $created)"
                [ "$id", box id; "$client", box $"fixture-{id}"; "$created", box timestamp ]
            |> ignore
            execute connection
                "INSERT INTO action_text_rich_texts(body, created_at, name, record_id, record_type, updated_at) VALUES ($body, $created, 'body', $id, 'Message', $created)"
                [ "$body", box body; "$created", box timestamp; "$id", box id ]
            |> ignore

    let private scalar path sql (parameters: (string * obj) list) =
        use connection = openConnection path SqliteOpenMode.ReadWrite
        use command = connection.CreateCommand()
        command.CommandText <- sql
        for name, value in parameters do command.Parameters.AddWithValue(name, value) |> ignore
        command.ExecuteScalar()

    let private scalarLong path sql parameters =
        Convert.ToInt64(scalar path sql parameters, CultureInfo.InvariantCulture)

    let private scalarString path sql parameters =
        Convert.ToString(scalar path sql parameters, CultureInfo.InvariantCulture)

    let private snapshot path =
        { Messages = scalarLong path "SELECT COUNT(*) FROM messages" []
          RichTextBodies = scalarLong path "SELECT COUNT(*) FROM action_text_rich_texts" []
          RoomUpdatedAt = scalarString path "SELECT updated_at FROM rooms WHERE id = 1" []
          FreshSessionLastActive = scalarString path "SELECT last_active_at FROM sessions WHERE id = 10" []
          DueSessionLastActive = scalarString path "SELECT last_active_at FROM sessions WHERE id = 11" []
          OrphanSessionLastActive = scalarString path "SELECT last_active_at FROM sessions WHERE id = 14" []
          FailedRefreshLastActive = scalarString path "SELECT last_active_at FROM sessions WHERE id = 12" [] }

    let private databaseDigest path =
        let tables =
            [ "users", "id"
              "sessions", "id"
              "bans", "id"
              "rooms", "id"
              "memberships", "id"
              "messages", "id"
              "action_text_rich_texts", "id" ]
        use connection = openConnection path SqliteOpenMode.ReadWrite
        use digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256)
        let append (text: string) = digest.AppendData(Encoding.UTF8.GetBytes text)
        for table, key in tables do
            append ($"TABLE:{table}\n")
            use command = connection.CreateCommand()
            command.CommandText <- $"SELECT * FROM {table} ORDER BY {key}"
            use reader = command.ExecuteReader()
            while reader.Read() do
                for index in 0 .. reader.FieldCount - 1 do
                    if reader.IsDBNull index then
                        append "N;"
                    else
                        let value = reader.GetValue index
                        let typeName = value.GetType().FullName
                        let text = Convert.ToString(value, CultureInfo.InvariantCulture)
                        append ($"V{typeName.Length}:{typeName}{text.Length}:{text};")
                append "\n"
        digest.GetHashAndReset() |> Convert.ToHexString |> fun value -> value.ToLowerInvariant()

    let private readCookieSecret () =
        use document = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "rails-cookie-vectors.json")))
        document.RootElement.GetProperty("secret_key_base").GetString()

    let private createSignedCookie (cookies: CookieService) token =
        cookies.SignSessionToken(token, now) |> expectOk "sign synthetic test session token"

    let private cookiePair name rawValue =
        let escaped = RackCookieEncoding.escape rawValue |> expectOk "Rack-escape test cookie"
        name + "=" + escaped

    let private getCookie (exchange: HttpExchange) name =
        exchange.SetCookies
        |> List.tryPick (fun header ->
            let first = header.Split(';', 2)[0]
            let parts = first.Split('=', 2)
            if parts.Length = 2 && parts[0] = name then
                RackCookieEncoding.unescape parts[1] |> Result.toOption
            else None)

    let private createEnvironment databasePath (clock: IClock) cookies =
        let campfire =
            CampfireHost.createProviderWithGuidSource databasePath clock (fun () -> Guid.NewGuid())
            |> expectOk "open existing message/room SQLite provider"
        let authentication =
            CampfireAuthenticationHost.createProvider databasePath
            |> expectOk "open existing authentication SQLite provider"
        let cookieProvider = CookieHost.createProvider cookies
        let browser = BrowserHost.createProvider()
        let combined = HostComposition.provider [ campfire; authentication; cookieProvider; browser ]
        let probe = ProviderProbe(combined)
        let manifest = HostComposition.manifest [ CookieHost.manifest; CampfireHost.manifest; CampfireAuthenticationHost.manifest; BrowserHost.manifest ]
        let mutable testFactoryCalls = 0
        let environment =
            { Manifest = manifest
              ProductionProvider = probe.AsProvider
              CreateTestProvider = fun () ->
                  testFactoryCalls <- testFactoryCalls + 1
                  Error HostOperationFailure.TestProviderUnavailable }
        environment, probe, (fun () -> testFactoryCalls)

    let private baseSpec methodName path =
        { Method = HttpMethod(methodName)
          Path = path
          Cookies = []
          CookieHeaderOverride = None
          ContentType = None
          Body = None
          Chunked = false
          Accept = None
          Origin = None
          FetchSite = None
          UserAgent = Some goodAgent
          ExtraHeaders = [] }

    let private send (client: HttpClient) (spec: RequestSpec) =
        use request = new HttpRequestMessage(spec.Method, Uri(client.BaseAddress, spec.Path))
        request.Headers.Host <- publicUri.Authority
        match spec.Accept with
        | Some value -> request.Headers.Accept.ParseAdd(value)
        | None -> ()
        match spec.Origin with Some value -> request.Headers.TryAddWithoutValidation("Origin", value) |> ignore | None -> ()
        match spec.FetchSite with Some value -> request.Headers.TryAddWithoutValidation("Sec-Fetch-Site", value) |> ignore | None -> ()
        match spec.UserAgent with Some value -> request.Headers.TryAddWithoutValidation("User-Agent", value) |> ignore | None -> ()
        for name, value in spec.ExtraHeaders do request.Headers.TryAddWithoutValidation(name, value) |> ignore
        match spec.CookieHeaderOverride with
        | Some header -> request.Headers.TryAddWithoutValidation("Cookie", header) |> ignore
        | None when not spec.Cookies.IsEmpty ->
            spec.Cookies |> List.map (fun (name, raw) -> cookiePair name raw) |> String.concat "; "
            |> fun value -> request.Headers.TryAddWithoutValidation("Cookie", value) |> ignore
        | None -> ()
        match spec.ContentType, spec.Body with
        | Some contentType, Some body ->
            if spec.Chunked then
                request.Content <- new UnknownLengthContent(body)
                request.Content.Headers.ContentType <- MediaTypeHeaderValue.Parse(contentType)
                request.Headers.TransferEncodingChunked <- Nullable true
                check (not request.Content.Headers.ContentLength.HasValue) "chunked request omits Content-Length"
                check
                    (request.Headers.TransferEncodingChunked.HasValue && request.Headers.TransferEncodingChunked.Value)
                    "chunked request explicitly uses HTTP transfer encoding"
            else
                request.Content <- new StringContent(body, Encoding.UTF8)
                request.Content.Headers.ContentType <- MediaTypeHeaderValue.Parse(contentType)
        | Some contentType, None ->
            request.Content <- new StringContent("", Encoding.UTF8)
            request.Content.Headers.ContentType <- MediaTypeHeaderValue.Parse(contentType)
        | None, Some body -> request.Content <- new StringContent(body, Encoding.UTF8)
        | None, None -> ()
        use response = client.SendAsync(request, HttpCompletionOption.ResponseContentRead).GetAwaiter().GetResult()
        let body = response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
        let values name =
            match response.Headers.TryGetValues name with
            | true, found -> found |> Seq.toList
            | false, _ -> []
        let exchange =
            { Status = int response.StatusCode
              Body = body
              Location = response.Headers.Location |> Option.ofObj |> Option.map string
              ContentType = response.Content.Headers.ContentType |> Option.ofObj |> Option.map string
              SetCookies = values "Set-Cookie" }
        appendTranscript spec exchange
        exchange

    let private expectStatus expected exchange =
        equal expected exchange.Status "HTTP status"

    let private expectTrace (probe: ProviderProbe) (expected: string list) =
        let actual = probe.Names
        let trace = JsonObject()
        let expectedJson = JsonArray()
        for name in expected do expectedJson.Add(JsonValue.Create(name))
        let actualJson = JsonArray()
        for name in actual do actualJson.Add(JsonValue.Create(name))
        trace["expected"] <- expectedJson
        trace["actual"] <- actualJson
        File.AppendAllText(operationTracePath, trace.ToJsonString() + Environment.NewLine, Encoding.UTF8)
        equal expected actual "HTTP provider call order"
        probe.Clear()

    let private assertCookieHasFlag (exchange: HttpExchange) cookieName flag =
        let header = exchange.SetCookies |> List.find (fun item -> item.StartsWith(cookieName + "=", StringComparison.Ordinal))
        check (header.Split(';') |> Array.exists (fun part -> part.Trim().Equals(flag, StringComparison.OrdinalIgnoreCase))) $"{cookieName} cookie includes {flag}"

    let private sourceHashes () =
        let files =
            [ "tests/AgentLang.Campfire.Http.Tests/AgentLang.Campfire.Http.Tests.fsproj"
              "tests/AgentLang.Campfire.Http.Tests/Program.fs"
              "tests/AgentLang.Campfire.Cookies.Tests/Fixtures/rails-cookie-vectors.json"
              "applications/campfire/http-types.flow"
              "applications/campfire/http.flow"
              "applications/campfire/http-tests.flow"
              "applications/campfire/browser-types.flow"
              "applications/campfire/Http/Application.fs"
              "applications/campfire/Http/CookieHost.fs"
              "applications/campfire/Http/ResponseRenderer.fs"
              "applications/campfire/Http/Transport.fs"
              "applications/campfire/Http/AgentLang.Campfire.Http.fsproj"
              "applications/campfire/campfire-types.flow"
              "applications/campfire/session-policy.flow"
              "applications/campfire/authentication-types.flow"
              "applications/campfire/authentication.flow"
              "applications/campfire/authentication-tests.flow"
              "applications/campfire/message-policy.flow"
              "applications/campfire/room-message.flow"
              "applications/campfire/room-message-tests.flow"
              "applications/campfire/request-policy.flow"
              "applications/campfire/browser-policy.flow"
              "applications/campfire/browser-tests.flow"
              "applications/campfire/Host/CampfireHost.fs"
              "applications/campfire/Host/AgentLang.Campfire.Host.fsproj"
              "applications/campfire/Persistence/SqliteProvider.fs"
              "applications/campfire/Persistence/AgentLang.Campfire.Persistence.fsproj"
              "applications/campfire/Authentication.Host/CampfireAuthenticationHost.fs"
              "applications/campfire/Authentication.Host/AgentLang.Campfire.Authentication.Host.fsproj"
              "applications/campfire/Authentication.Persistence/SqliteProvider.fs"
              "applications/campfire/Authentication.Persistence/AgentLang.Campfire.Authentication.Persistence.fsproj"
              "applications/campfire/Browser/BrowserHost.fs"
              "applications/campfire/Browser/AgentLang.Campfire.Browser.Host.fsproj"
              "applications/campfire/Browser/AgentLang.Campfire.Browser.fsproj"
              "applications/campfire/Browser/RubyVersion.fs"
              "applications/campfire/Browser/UserAgentNormalizer.fs"
              "applications/campfire/Cookies/CookieService.fs"
              "applications/campfire/Cookies/AgentLang.Campfire.Cookies.fsproj"
              "src/AgentLang.Core/Core.fs"
              "src/AgentLang.Core/Runtime.fs"
              "src/AgentLang.Core/FlowParser.fs" ]
        files
        |> List.map (fun relative ->
            let path = Path.Combine(repositoryRoot, relative.Replace('/', Path.DirectorySeparatorChar))
            use stream = File.OpenRead(path)
            relative, SHA256.HashData(stream) |> Convert.ToHexString |> fun value -> value.ToLowerInvariant())

    let private checkHttpBehavior (client: HttpClient) (cookies: CookieService) (probe: ProviderProbe) =
        let token name = createSignedCookie cookies name

        currentCheck <- "member GET renders bounded newest page and escapes markup"
        probe.Clear()
        let room =
            send client
                { (baseSpec "GET" "/rooms/1") with
                    Cookies = [ "session_token", token "fresh-token" ]
                    Accept = Some "text/html"
                    FetchSite = Some "same-origin" }
        expectStatus 200 room
        check (room.Body.Contains("<h1>Main room</h1>", StringComparison.Ordinal)) "GET contains the authorized room title"
        equal 40 (room.Body.Split("<article id=\"message_", StringSplitOptions.None).Length - 1) "GET renders exactly forty messages"
        check (room.Body.Contains("message_6", StringComparison.Ordinal)) "GET contains the oldest row in the newest forty"
        check (room.Body.Contains("message_45", StringComparison.Ordinal)) "GET contains the newest row"
        check (not (room.Body.Contains("message_5", StringComparison.Ordinal))) "GET excludes rows older than the newest forty"
        check (room.Body.Contains("&lt;script&gt;", StringComparison.Ordinal)) "stored markup is HTML-escaped"
        check (not (room.Body.Contains("<script>", StringComparison.Ordinal))) "stored markup is not emitted as executable markup"
        expectTrace probe
            [ "campfire.cookies.verify-session-token"; "campfire.sessions.find-by-token"; "campfire.users.find-by-id"
              "campfire.browser.version-at-least?"; "campfire.rooms.find-for-user"; "campfire.membership.exists?"; "campfire.messages.last-page" ]
        check (room.SetCookies |> List.exists (fun value -> value.StartsWith("last_room=", StringComparison.Ordinal))) "first successful room visit writes the last-room cookie"

        currentCheck <- "unauthenticated room redirect preserves encrypted existing state and return URL"
        let initialState = JsonObject()
        initialState["retained-marker"] <- JsonValue.Create("kept-across-login-redirect")
        let encryptedState = cookies.EncryptRailsSession(initialState, now) |> expectOk "encrypt synthetic starting Rails session"
        probe.Clear()
        let login =
            send client
                { (baseSpec "GET" "/rooms/1?view=compact&x=1") with
                    Cookies = [ "_campfire_session", encryptedState ]
                    FetchSite = Some "same-origin"
                    ExtraHeaders = [ "X-Forwarded-Host", "attacker.invalid"; "X-Forwarded-Proto", "https" ] }
        expectStatus 302 login
        equal (Some "/session/new") login.Location "unauthenticated route redirects to fixed login path"
        let returnedState =
            getCookie login "_campfire_session"
            |> Option.bind (fun value -> cookies.DecryptRailsSession(value, now))
            |> Option.defaultWith (fun () -> failwith "redirect response did not produce a decryptable Rails session cookie")
        equal "kept-across-login-redirect" (returnedState["retained-marker"].GetValue<string>()) "existing encrypted session state survives redirect mutation"
        equal "http://campfire.test/rooms/1?view=compact&x=1" (returnedState["return_to_after_authenticating"].GetValue<string>()) "encrypted return URL preserves route and query while using configured authority"
        assertCookieHasFlag login "_campfire_session" "HttpOnly"
        expectTrace probe []

        currentCheck <- "signed Int64 room identifiers reach ordinary scoped lookups"
        for id in [ 0L; -1L; Int64.MinValue; Int64.MaxValue ] do
            probe.Clear()
            let response =
                send client
                    { (baseSpec "GET" ($"/rooms/{id.ToString(CultureInfo.InvariantCulture)}")) with
                        Cookies = [ "session_token", token "fresh-token" ]
                        FetchSite = Some "same-origin" }
            expectStatus 302 response
            equal (Some "/") response.Location $"room id {id} is an ordinary missing-or-inaccessible room lookup"
            expectTrace probe
                [ "campfire.cookies.verify-session-token"; "campfire.sessions.find-by-token"; "campfire.users.find-by-id"
                  "campfire.browser.version-at-least?"; "campfire.rooms.find-for-user" ]

        currentCheck <- "malformed and overflowing route identifiers fail before provider dispatch"
        for path in [ "/rooms/not-an-id"; "/rooms/9223372036854775808"; "/rooms/-9223372036854775809" ] do
            probe.Clear()
            let response = send client (baseSpec "GET" path)
            expectStatus 400 response
            expectTrace probe []

        currentCheck <- "unmapped paths and unsupported methods stay in the Kestrel transport"
        probe.Clear()
        let unknownPath = send client (baseSpec "GET" "/not-a-campfire-route")
        expectStatus 404 unknownPath
        expectTrace probe []
        probe.Clear()
        let unsupportedMethod = send client (baseSpec "PUT" "/rooms/1")
        expectStatus 405 unsupportedMethod
        expectTrace probe []

        currentCheck <- "nonmember room redirects to root after session restoration"
        probe.Clear()
        let noAccess =
            send client
                { (baseSpec "GET" "/rooms/1") with
                    Cookies = [ "session_token", token "no-access-token" ]
                    FetchSite = Some "same-origin" }
        expectStatus 302 noAccess
        equal (Some "/") noAccess.Location "authenticated nonmember is redirected to root"
        check (noAccess.SetCookies |> List.exists (fun value -> value.StartsWith("_campfire_session=", StringComparison.Ordinal))) "root redirect sets encrypted flash state"
        expectTrace probe
            [ "campfire.cookies.verify-session-token"; "campfire.sessions.find-by-token"; "campfire.users.find-by-id"
              "campfire.browser.version-at-least?"; "campfire.rooms.find-for-user" ]

        currentCheck <- "message POST generates absent UUID and derives creator from the session"
        probe.Clear()
        let countBeforeCreate = scalarLong databasePath "SELECT COUNT(*) FROM messages" []
        let create =
            send client
                { (baseSpec "POST" "/rooms/1/messages") with
                    Cookies = [ "session_token", token "fresh-token" ]
                    ContentType = Some "application/x-www-form-urlencoded"
                    Body = Some "message%5Bbody%5D=posted+from+member&message%5Bcreator_id%5D=3"
                    Accept = Some "text/vnd.turbo-stream.html"
                    Origin = Some (publicUri.GetLeftPart(UriPartial.Authority))
                    FetchSite = Some "same-origin" }
        expectStatus 200 create
        check (create.ContentType |> Option.exists (fun value -> value.StartsWith("text/vnd.turbo-stream.html", StringComparison.OrdinalIgnoreCase))) "successful POST renders a Turbo Stream response"
        check (create.Body.Contains("posted from member", StringComparison.Ordinal)) "successful POST includes the created message"
        equal (countBeforeCreate + 1L) (scalarLong databasePath "SELECT COUNT(*) FROM messages" []) "successful POST persists exactly one message"
        let generatedClientId = scalarString databasePath "SELECT client_message_id FROM messages ORDER BY id DESC LIMIT 1" []
        let mutable parsedGuid = Guid.Empty
        check (Guid.TryParseExact(generatedClientId, "D", &parsedGuid)) "missing client ID is replaced with a canonical UUID"
        equal 1L (scalarLong databasePath "SELECT creator_id FROM messages ORDER BY id DESC LIMIT 1" []) "creator comes from the authenticated session even when the form submits creator_id"
        equal "posted from member" (scalarString databasePath "SELECT body FROM action_text_rich_texts ORDER BY id DESC LIMIT 1" []) "posted rich-text body is persisted"
        expectTrace probe
            [ "campfire.bans.ip-banned?"; "campfire.cookies.verify-session-token"; "campfire.sessions.find-by-token"
              "campfire.users.find-by-id"; "campfire.browser.version-at-least?"; "campfire.rooms.find-for-user"
              "campfire.membership.exists?"; "campfire.messages.create" ]

        currentCheck <- "message POST preserves an explicitly empty client ID"
        probe.Clear()
        let beforeEmpty = scalarLong databasePath "SELECT COUNT(*) FROM messages" []
        let emptyId =
            send client
                { (baseSpec "POST" "/rooms/1/messages") with
                    Cookies = [ "session_token", token "fresh-token" ]
                    ContentType = Some "application/x-www-form-urlencoded"
                    Body = Some "message%5Bclient_message_id%5D=&message%5Bbody%5D=empty+id"
                    Accept = Some "text/vnd.turbo-stream.html"
                    Origin = Some (publicUri.GetLeftPart(UriPartial.Authority))
                    FetchSite = Some "same-origin" }
        expectStatus 200 emptyId
        equal (beforeEmpty + 1L) (scalarLong databasePath "SELECT COUNT(*) FROM messages" []) "empty-ID POST inserts one row"
        equal "" (scalarString databasePath "SELECT client_message_id FROM messages ORDER BY id DESC LIMIT 1" []) "Some empty client ID is preserved verbatim"
        expectTrace probe
            [ "campfire.bans.ip-banned?"; "campfire.cookies.verify-session-token"; "campfire.sessions.find-by-token"
              "campfire.users.find-by-id"; "campfire.browser.version-at-least?"; "campfire.rooms.find-for-user"
              "campfire.membership.exists?"; "campfire.messages.create" ]

        currentCheck <- "CSRF failure occurs after due-session refresh and before message creation"
        probe.Clear()
        let beforeCsrf = scalarLong databasePath "SELECT COUNT(*) FROM messages" []
        let csrf =
            send client
                { (baseSpec "POST" "/rooms/1/messages") with
                    Cookies = [ "session_token", token "due-token" ]
                    ContentType = Some "application/x-www-form-urlencoded"
                    Body = Some "message%5Bbody%5D=must+not+persist"
                    Origin = Some "http://attacker.invalid"
                    FetchSite = Some "same-origin"
                    ExtraHeaders = [ "X-Forwarded-For", "203.0.113.99" ] }
        expectStatus 422 csrf
        equal (beforeCsrf) (scalarLong databasePath "SELECT COUNT(*) FROM messages" []) "CSRF failure inserts no message"
        equal "2026-01-01 12:00:00" (scalarString databasePath "SELECT last_active_at FROM sessions WHERE id = 11" []) "due session refresh commits before CSRF rejection"
        equal "127.0.0.1" (scalarString databasePath "SELECT ip_address FROM sessions WHERE id = 11" []) "due refresh uses the transport remote address and ignores forwarded headers"
        equal (Some(token "due-token")) (getCookie csrf "session_token") "CSRF response refreshes the exact persisted session token"
        check (csrf.SetCookies |> List.exists (fun value -> value.StartsWith("session_token=", StringComparison.Ordinal))) "CSRF response returns refreshed session token cookie"
        assertCookieHasFlag csrf "session_token" "HttpOnly"
        expectTrace probe
            [ "campfire.bans.ip-banned?"; "campfire.cookies.verify-session-token"; "campfire.sessions.find-by-token"
              "campfire.sessions.refresh-before"; "campfire.users.find-by-id" ]

        currentCheck <- "Accept negotiation follows the fixed Turbo response policy after persistence"
        let createWithNegotiation (label: string) (path: string) (accept: string option) (expectedStatus: int) =
            probe.Clear()
            let before = scalarLong databasePath "SELECT COUNT(*) FROM messages" []
            let response =
                send client
                    { (baseSpec "POST" path) with
                        Cookies = [ "session_token", token "fresh-token" ]
                        ContentType = Some "application/x-www-form-urlencoded"
                        Body = Some("message%5Bbody%5D=" + Uri.EscapeDataString(label))
                        Accept = accept
                        Origin = Some (publicUri.GetLeftPart(UriPartial.Authority))
                        FetchSite = Some "same-origin" }
            expectStatus expectedStatus response
            equal (before + 1L) (scalarLong databasePath "SELECT COUNT(*) FROM messages" []) $"{label} is persisted before response negotiation"
            equal label (scalarString databasePath "SELECT body FROM action_text_rich_texts ORDER BY id DESC LIMIT 1" []) $"{label} remains stored"
            expectTrace probe
                [ "campfire.bans.ip-banned?"; "campfire.cookies.verify-session-token"; "campfire.sessions.find-by-token"
                  "campfire.users.find-by-id"; "campfire.browser.version-at-least?"; "campfire.rooms.find-for-user"
                  "campfire.membership.exists?"; "campfire.messages.create" ]
            response
        let notAcceptable = createWithNegotiation "html response is not available" "/rooms/1/messages" (Some "text/html") 406
        let absentAccept = createWithNegotiation "missing Accept is not available" "/rooms/1/messages" None 406
        let browserHtmlWildcard =
            createWithNegotiation "browser-like HTML wildcard is not Turbo" "/rooms/1/messages"
                (Some "text/html,application/xhtml+xml,application/xml;q=0.9,image/avif,image/webp,*/*;q=0.8") 406
        let explicitWildcard = createWithNegotiation "explicit wildcard enables Turbo" "/rooms/1/messages" (Some "*/*") 200
        let textWildcard = createWithNegotiation "text wildcard enables Turbo" "/rooms/1/messages" (Some "text/*") 200
        let zeroQualityTurbo = createWithNegotiation "q-zero Turbo remains offered" "/rooms/1/messages" (Some "text/vnd.turbo-stream.html;q=0") 200
        let queryFormat = createWithNegotiation "format query enables Turbo" "/rooms/1/messages?format=turbo_stream" None 200
        for response in [ explicitWildcard; textWildcard; zeroQualityTurbo; queryFormat ] do
            check (response.ContentType |> Option.exists (fun value -> value.StartsWith("text/vnd.turbo-stream.html", StringComparison.OrdinalIgnoreCase))) "Turbo-compatible negotiation renders Turbo Stream"
        check (notAcceptable.Body = "") "406 response does not render the created message body"
        check (absentAccept.Body = "") "missing Accept response has an empty body after the write"
        check (browserHtmlWildcard.Body = "") "browser-like wildcard response has an empty body after the write"

        currentCheck <- "message insert failure rolls back before room touch"
        probe.Clear()
        let beforeFailure = snapshot databasePath
        let failedCreate =
            send client
                { (baseSpec "POST" "/rooms/1/messages") with
                    Cookies = [ "session_token", token "fresh-token" ]
                    ContentType = Some "application/x-www-form-urlencoded"
                    Body = Some "message%5Bbody%5D=abort-after-insert"
                    Accept = Some "text/vnd.turbo-stream.html"
                    Origin = Some (publicUri.GetLeftPart(UriPartial.Authority))
                    FetchSite = Some "same-origin" }
        expectStatus 500 failedCreate
        let afterFailure = snapshot databasePath
        equal beforeFailure.Messages afterFailure.Messages "failed message transaction rolls back the message insert"
        equal beforeFailure.RichTextBodies afterFailure.RichTextBodies "failed message transaction leaves rich-text count unchanged"
        equal beforeFailure.RoomUpdatedAt afterFailure.RoomUpdatedAt "failed message transaction does not touch the room"
        expectTrace probe
            [ "campfire.bans.ip-banned?"; "campfire.cookies.verify-session-token"; "campfire.sessions.find-by-token"
              "campfire.users.find-by-id"; "campfire.browser.version-at-least?"; "campfire.rooms.find-for-user"
              "campfire.membership.exists?"; "campfire.messages.create" ]

        currentCheck <- "malformed and unsupported forms fail before any message write"
        let beforeMalformed = scalarLong databasePath "SELECT COUNT(*) FROM messages" []
        probe.Clear()
        let duplicateCookie =
            cookiePair "session_token" (token "fresh-token") + "; " + cookiePair "session_token" (token "fresh-token")
        let duplicateCookieResponse =
            send client
                { (baseSpec "GET" "/rooms/1") with
                    CookieHeaderOverride = Some duplicateCookie }
        expectStatus 400 duplicateCookieResponse
        expectTrace probe []

        let invalidForm =
            send client
                { (baseSpec "POST" "/rooms/1/messages") with
                    Cookies = [ "session_token", token "fresh-token" ]
                    ContentType = Some "application/x-www-form-urlencoded"
                    Body = Some "message%5Bbody%5D=one&message%5Bbody%5D=two"
                    Origin = Some (publicUri.GetLeftPart(UriPartial.Authority))
                    FetchSite = Some "same-origin" }
        expectStatus 400 invalidForm
        let unsupportedMedia =
            send client
                { (baseSpec "POST" "/rooms/1/messages") with
                    Cookies = [ "session_token", token "fresh-token" ]
                    ContentType = Some "text/plain"
                    Body = Some "plain text" }
        expectStatus 415 unsupportedMedia
        let attachment =
            send client
                { (baseSpec "POST" "/rooms/1/messages") with
                    Cookies = [ "session_token", token "fresh-token" ]
                    ContentType = Some "application/x-www-form-urlencoded"
                    Body = Some "message%5Battachment%5D%5Bsigned_blob_id%5D=opaque"
                    Origin = Some (publicUri.GetLeftPart(UriPartial.Authority))
                    FetchSite = Some "same-origin" }
        expectStatus 422 attachment
        equal beforeMalformed (scalarLong databasePath "SELECT COUNT(*) FROM messages" []) "invalid, unsupported, and attachment forms perform no writes"
        let authenticatedRoomLookup =
            [ "campfire.bans.ip-banned?"; "campfire.cookies.verify-session-token"; "campfire.sessions.find-by-token"; "campfire.users.find-by-id"
              "campfire.browser.version-at-least?"; "campfire.rooms.find-for-user" ]
        expectTrace probe (List.concat [ authenticatedRoomLookup; authenticatedRoomLookup; authenticatedRoomLookup ])

        currentCheck <- "duplicate critical Origin headers fail CSRF before room lookup or message creation"
        probe.Clear()
        let beforeDuplicateOrigin = scalarLong databasePath "SELECT COUNT(*) FROM messages" []
        let duplicateOrigin =
            send client
                { (baseSpec "POST" "/rooms/1/messages") with
                    Cookies = [ "session_token", token "fresh-token" ]
                    ContentType = Some "application/x-www-form-urlencoded"
                    Body = Some "message%5Bbody%5D=duplicate+origin"
                    FetchSite = Some "same-origin"
                    ExtraHeaders =
                        [ "Origin", publicUri.GetLeftPart(UriPartial.Authority)
                          "Origin", publicUri.GetLeftPart(UriPartial.Authority) ] }
        expectStatus 422 duplicateOrigin
        equal beforeDuplicateOrigin (scalarLong databasePath "SELECT COUNT(*) FROM messages" []) "duplicate Origin rejection writes no message"
        expectTrace probe
            [ "campfire.bans.ip-banned?"; "campfire.cookies.verify-session-token"; "campfire.sessions.find-by-token"
              "campfire.users.find-by-id" ]

        currentCheck <- "missing message room returns the fixed escaped deleted-room response"
        probe.Clear()
        let beforeMissingRoom = scalarLong databasePath "SELECT COUNT(*) FROM messages" []
        let missingRoom =
            send client
                { (baseSpec "POST" "/rooms/999/messages") with
                    Cookies = [ "session_token", token "fresh-token" ]
                    ContentType = Some "application/x-www-form-urlencoded"
                    Body = Some "message%5Bbody%5D=must+not+be+stored"
                    Accept = Some "text/vnd.turbo-stream.html"
                    Origin = Some (publicUri.GetLeftPart(UriPartial.Authority))
                    FetchSite = Some "same-origin" }
        expectStatus 200 missingRoom
        check (missingRoom.ContentType |> Option.exists (fun value -> value.StartsWith("text/html", StringComparison.OrdinalIgnoreCase))) "deleted room response uses the fixed HTML view"
        check (missingRoom.Body.Contains("This room was deleted.", StringComparison.Ordinal)) "deleted room response keeps the exact notice"
        equal beforeMissingRoom (scalarLong databasePath "SELECT COUNT(*) FROM messages" []) "missing-room POST does not create a message"
        expectTrace probe
            [ "campfire.bans.ip-banned?"; "campfire.cookies.verify-session-token"; "campfire.sessions.find-by-token"
              "campfire.users.find-by-id"; "campfire.browser.version-at-least?"; "campfire.rooms.find-for-user" ]

        currentCheck <- "orphan session refreshes before missing-user failure and skips room lookup"
        probe.Clear()
        let orphan =
            send client
                { (baseSpec "GET" "/rooms/1") with
                    Cookies = [ "session_token", token "orphan-token" ]
                    FetchSite = Some "same-origin" }
        expectStatus 500 orphan
        equal "2026-01-01 12:00:00" (scalarString databasePath "SELECT last_active_at FROM sessions WHERE id = 14" []) "orphan session refresh is durable before the missing user result"
        check (orphan.SetCookies |> List.exists (fun value -> value.StartsWith("session_token=", StringComparison.Ordinal))) "orphan error retains the due refresh cookie"
        expectTrace probe
            [ "campfire.cookies.verify-session-token"; "campfire.sessions.find-by-token"; "campfire.sessions.refresh-before"
              "campfire.users.find-by-id"; "campfire.browser.version-at-least?" ]

        currentCheck <- "failed session refresh stops before user lookup and preserves database row"
        probe.Clear()
        let blocked =
            send client
                { (baseSpec "GET" "/rooms/1") with
                    Cookies = [ "session_token", token "blocked-refresh-token" ]
                    FetchSite = Some "same-origin" }
        expectStatus 500 blocked
        equal "2025-12-31 00:00:00" (scalarString databasePath "SELECT last_active_at FROM sessions WHERE id = 12" []) "aborted session refresh leaves its timestamp unchanged"
        check (not (blocked.SetCookies |> List.exists (fun value -> value.StartsWith("session_token=", StringComparison.Ordinal)))) "failed refresh does not emit a refreshed token"
        expectTrace probe [ "campfire.cookies.verify-session-token"; "campfire.sessions.find-by-token"; "campfire.sessions.refresh-before" ]

        currentCheck <- "body and cookie limits reject before language dispatch"
        probe.Clear()
        let tooLargeBody = String('a', int Transport.MaximumBodyBytes + 1)
        let bodyLimit =
            send client
                { (baseSpec "POST" "/rooms/1/messages") with
                    Cookies = [ "session_token", token "fresh-token" ]
                    ContentType = Some "application/x-www-form-urlencoded"
                    Body = Some tooLargeBody }
        expectStatus 413 bodyLimit
        expectTrace probe []
        let beforeChunked = scalarLong databasePath "SELECT COUNT(*) FROM messages" []
        let chunkedBodyLimit =
            send client
                { (baseSpec "POST" "/rooms/1/messages") with
                    Cookies = [ "session_token", token "fresh-token" ]
                    ContentType = Some "application/x-www-form-urlencoded"
                    Body = Some tooLargeBody
                    Chunked = true }
        expectStatus 413 chunkedBodyLimit
        equal beforeChunked (scalarLong databasePath "SELECT COUNT(*) FROM messages" []) "oversized chunked request creates no message"
        expectTrace probe []
        let cookieLimit =
            send client
                { (baseSpec "GET" "/rooms/1") with
                    CookieHeaderOverride = Some("unrecognized=" + String('c', 17000)) }
        expectStatus 431 cookieLimit
        expectTrace probe []

        currentCheck <- "unsafe-IP ban precedes cookie verification while GET omits ban lookup"
        executeAtPath
            databasePath
            "INSERT INTO bans(id, user_id, ip_address, created_at, updated_at) VALUES (1, 1, '127.0.0.1', '2025-01-01 00:00:00', '2025-01-01 00:00:00')"
            []
        |> ignore
        probe.Clear()
        let bannedPost =
            send client
                { (baseSpec "POST" "/rooms/1/messages") with
                    Cookies = [ "session_token", token "fresh-token" ]
                    ContentType = Some "application/x-www-form-urlencoded"
                    Body = Some "message%5Bbody%5D=banned"
                    Origin = Some (publicUri.GetLeftPart(UriPartial.Authority))
                    FetchSite = Some "same-origin"
                    ExtraHeaders = [ "X-Forwarded-For", "203.0.113.99" ] }
        expectStatus 429 bannedPost
        expectTrace probe [ "campfire.bans.ip-banned?" ]
        let getWhileBanned =
            send client
                { (baseSpec "GET" "/rooms/1") with
                    Cookies = [ "session_token", token "fresh-token" ]
                    FetchSite = Some "same-origin"
                    ExtraHeaders = [ "X-Forwarded-For", "203.0.113.99" ] }
        expectStatus 200 getWhileBanned
        expectTrace probe
            [ "campfire.cookies.verify-session-token"; "campfire.sessions.find-by-token"; "campfire.users.find-by-id"
              "campfire.browser.version-at-least?"; "campfire.rooms.find-for-user"; "campfire.membership.exists?"; "campfire.messages.last-page" ]

    let private runHttpApplication () =
        Directory.CreateDirectory(runDirectory) |> ignore
        File.WriteAllText(transcriptPath, "", Encoding.UTF8)
        File.WriteAllText(operationTracePath, "", Encoding.UTF8)
        seedFixture databasePath
        let secret = readCookieSecret()
        use cookies = CookieService.Create(secret) |> expectOk "create CookieService with published synthetic test vector"
        let clock = FixedClock(now)
        let environment, providerProbe, testFactoryCalls = createEnvironment databasePath (clock :> IClock) cookies
        let sourceDirectory = Path.Combine(repositoryRoot, "applications", "campfire")
        let application = Application.create projectDirectory sourceDirectory environment
        let descriptionArgs = JsonObject()
        descriptionArgs["word"] <- JsonValue.Create("campfire.authentication.sign-in")
        let signInDescription = application.Engine.Dispatch("describe", descriptionArgs)
        check ((signInDescription["ok"]).GetValue<bool>() && (signInDescription["data"]["maturity"]).GetValue<string>() = "library") "startup makes the tested sign-in library discoverable"
        equal 0 (testFactoryCalls()) "fully scoped startup qualification never creates a provider or falls back to production"

        let options =
            { PublicBaseUri = publicUri
              ForceSsl = false
              Cookies = cookies
              Clock = fun () -> now
              NormalizeBrowser = Application.normalizeBrowser
              Invoke = application.Invoke }

        let builder = WebApplication.CreateBuilder([||])
        builder.WebHost.UseUrls("http://127.0.0.1:0") |> ignore
        builder.Logging.ClearProviders() |> ignore
        use web = builder.Build()
        Transport.mapRoutes web options
        web.StartAsync().GetAwaiter().GetResult()
        try
            let server = web.Services.GetRequiredService<IServer>()
            let feature = server.Features.Get<IServerAddressesFeature>()
            if isNull feature || feature.Addresses.Count = 0 then failwith "Kestrel did not expose its ephemeral loopback address"
            let address = feature.Addresses |> Seq.head
            use handler = new HttpClientHandler(AllowAutoRedirect = false, UseCookies = false, UseProxy = false)
            use client = new HttpClient(handler)
            client.Timeout <- TimeSpan.FromSeconds(10.0)
            client.BaseAddress <- Uri address
            check (client.BaseAddress.IsLoopback) "Kestrel bound an ephemeral loopback endpoint"
            check (client.BaseAddress.Port > 0) "Kestrel selected a nonzero ephemeral port"
            check (address.StartsWith("http://127.0.0.1:", StringComparison.OrdinalIgnoreCase)) "Kestrel uses the IPv4 loopback address expected by the ban fixture"

            checkHttpBehavior client cookies providerProbe

            currentCheck <- "same dictionary can be reopened without database mutation"
            let before = snapshot databasePath
            let databaseDigestBefore = databaseDigest databasePath
            let currentStore = Path.Combine(projectDirectory, ".agentlang", "store", "CURRENT")
            let dictionaryPointerBefore = File.ReadAllBytes currentStore
            let providerCallsBeforeRestart = providerProbe.Calls.Length
            let restarted = Application.create projectDirectory sourceDirectory environment
            equal before (snapshot databasePath) "restarting the application preserves the asserted domain summary"
            equal databaseDigestBefore (databaseDigest databasePath) "restarting the application leaves every fixture table row unchanged"
            equal providerCallsBeforeRestart providerProbe.Calls.Length "startup/reload does not execute database operations"
            check (dictionaryPointerBefore.AsSpan().SequenceEqual(File.ReadAllBytes currentStore)) "second startup reuses the same persisted dictionary generation"
            let words = restarted.Engine.Dispatch("words", JsonObject())
            check (words["ok"].GetValue<bool>()) "fresh engine can read the existing published language dictionary"
            let dataNode = words["data"]
            let wordEntriesNode = dataNode["words"]
            let wordEntries = wordEntriesNode.AsArray()
            check
                (wordEntries |> Seq.exists (fun word ->
                    let nameNode = word["name"]
                    nameNode.GetValue<string>() = "campfire.http.dispatch"))
                "reloaded dictionary retains the fixed HTTP entrypoint"
            equal 0 (testFactoryCalls()) "repeat startup still does not construct a test provider"
        finally
            web.StopAsync().GetAwaiter().GetResult()
            providerProbe.AsProvider.Dispose()

    let private writeReceipt (outcome: string) (failure: string option) =
        Directory.CreateDirectory(runDirectory) |> ignore
        let root = JsonObject()
        root["runId"] <- jsonStringNode runId
        root["outcome"] <- JsonValue.Create outcome
        root["startedAtUtc"] <- JsonValue.Create(startedAt.ToString("O", CultureInfo.InvariantCulture))
        root["finishedAtUtc"] <- JsonValue.Create(DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture))
        root["assertions"] <- JsonValue.Create assertions
        root["currentCheck"] <- JsonValue.Create currentCheck
        root["failure"] <- optionalStringNode failure
        root["failureMessage"] <- optionalStringNode failureMessage
        root["transcript"] <- JsonValue.Create(Path.GetFileName transcriptPath)
        root["providerTraces"] <- JsonValue.Create(Path.GetFileName operationTracePath)
        let sources = JsonObject()
        for relative, hash in sourceHashes() do sources[relative] <- JsonValue.Create hash
        root["sourceSha256"] <- sources
        File.WriteAllText(receiptPath, root.ToJsonString(JsonSerializerOptions(WriteIndented = true)) + Environment.NewLine, Encoding.UTF8)
        printfn "RECEIPT %s" receiptPath

    [<EntryPoint>]
    let main _ =
        Directory.CreateDirectory(runDirectory) |> ignore
        let outcome =
            try
                runHttpApplication ()
                printfn "PASS Campfire Kestrel HTTP integration (%d assertions)" assertions
                "passed"
            with error ->
                failureMessage <- Some error.Message
                eprintfn "FAIL %s: %s" currentCheck error.Message
                eprintfn "%s" (error.ToString())
                "failed"
        try
            writeReceipt outcome (if outcome = "passed" then None else Some currentCheck)
        with error -> eprintfn "Could not write receipt: %s" error.Message
        if outcome = "passed" then 0 else 1
