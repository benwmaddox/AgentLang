namespace AgentLang.Campfire.Http.Tests

open System
open System.Globalization
open System.IO
open System.Net
open System.Net.Http
open System.Net.Http.Headers
open System.Security.Cryptography
open System.Text
open System.Text.RegularExpressions
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
type private FixedClock(initialInstant: DateTimeOffset) =
    let mutable instant = initialInstant

    member _.CurrentInstant = instant
    member _.Advance(duration: TimeSpan) = instant <- instant.Add duration

    interface IClock with
        member _.UtcNow() = instant

[<Sealed>]
type private ProviderProbe(provider: HostOperationProvider) =
    let calls = ResizeArray<string * Value list>()
    let mutable disposeCalls = 0

    let rec redactValue = function
        | StringValue _ -> StringValue "<redacted:string>"
        | ListValue (itemType, values) -> ListValue(itemType, List.map redactValue values)
        | OptionValue (itemType, value) -> OptionValue(itemType, Option.map redactValue value)
        | ResultValue (okType, errorType, result) -> ResultValue(okType, errorType, Result.map redactValue result |> Result.mapError redactValue)
        | RecordValue (name, fields) -> RecordValue(name, Map.map (fun _ value -> redactValue value) fields)
        | NamedValue (name, value) -> NamedValue(name, redactValue value)
        | value -> value

    member _.Calls = calls |> Seq.toList
    member _.Names = calls |> Seq.map fst |> Seq.toList
    member _.Clear() = calls.Clear()
    member _.DisposeCalls = disposeCalls

    member _.AsProvider =
        { OperationIds = provider.OperationIds
          Invoke = fun operation values ->
              let (HostOperationId name) = operation
              calls.Add((name, List.map redactValue values))
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
    let private runDirectory = Path.Combine(repositoryRoot, ".agentlang", "campfire-logout-245", "http-tests", "runs", runId)
    let private projectDirectory = Path.Combine(runDirectory, "project")
    let private databasePath = Path.Combine(runDirectory, "campfire.sqlite3")
    let private transcriptPath = Path.Combine(runDirectory, "http-transcript.jsonl")
    let private operationTracePath = Path.Combine(runDirectory, "provider-traces.jsonl")
    let private receiptPath = Path.Combine(runDirectory, "receipt.json")
    let private publicUri = Uri("http://campfire.test")
    let private now = DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero)
    let private goodAgent = "Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0.0.0 Safari/537.36"
    let private invalidMarkup = "<script>alert('unsafe')</script>"
    let private logoutFormEndpoint = "https://push.example.test/subscriptions/form"
    let private logoutQueryEndpoint = "https://push.example.test/subscriptions/query"
    let private logoutFailureEndpoint = "https://push.example.test/subscriptions/failure"
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
        let safePath =
            if request.Path.StartsWith("/session", StringComparison.Ordinal) then
                let query = request.Path.IndexOf('?')
                if query >= 0 then request.Path.Substring(0, query) + "?<redacted>" else request.Path
            else request.Path
        requestNode["path"] <- JsonValue.Create safePath
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
        requestNode["body"] <-
            if request.Path.StartsWith("/session", StringComparison.Ordinal) && request.Body.IsSome then
                jsonStringNode "<redacted-session-form>"
            else optionalStringNode request.Body

        let responseNode = JsonObject()
        responseNode["status"] <- JsonValue.Create response.Status
        responseNode["location"] <- optionalStringNode response.Location
        responseNode["contentType"] <- optionalStringNode response.ContentType
        responseNode["body"] <-
            JsonValue.Create(
                if request.Path.StartsWith("/session", StringComparison.Ordinal) then "<redacted-session-response>"
                else response.Body)
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
            "CREATE TABLE users (id INTEGER PRIMARY KEY, name TEXT NOT NULL, bio TEXT, role INTEGER NOT NULL, status INTEGER NOT NULL, bot_token TEXT, email_address TEXT, password_digest TEXT, created_at TEXT NOT NULL, updated_at TEXT NOT NULL); " +
            "CREATE UNIQUE INDEX users_bot_token_unique ON users(bot_token); " +
            "CREATE TABLE sessions (id INTEGER PRIMARY KEY, user_id INTEGER NOT NULL REFERENCES users(id), token TEXT NOT NULL UNIQUE, ip_address TEXT, user_agent TEXT, last_active_at TEXT NOT NULL, created_at TEXT NOT NULL, updated_at TEXT NOT NULL); " +
            "CREATE TABLE push_subscriptions (id INTEGER PRIMARY KEY, user_id INTEGER NOT NULL REFERENCES users(id), endpoint TEXT NOT NULL, p256dh_key TEXT NOT NULL, auth_key TEXT NOT NULL, created_at TEXT NOT NULL, updated_at TEXT NOT NULL); " +
            "CREATE INDEX push_subscriptions_user_endpoint_index ON push_subscriptions(user_id, endpoint); " +
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
            "CREATE TRIGGER reject_test_push_subscription_delete BEFORE DELETE ON push_subscriptions WHEN OLD.endpoint = 'https://push.example.test/subscriptions/failure' BEGIN SELECT RAISE(ABORT, 'test-only push subscription delete failure'); END; " +
            "CREATE TRIGGER reject_test_body BEFORE INSERT ON action_text_rich_texts WHEN NEW.body = 'abort-after-insert' BEGIN SELECT RAISE(ABORT, 'test-only message write failure'); END"
        command.ExecuteNonQuery() |> ignore

        for id, name, role, status, botToken, email, passwordDigest in
            [ 1, "member", 0, 0, None, Some "member@example.test", Some "$2a$12$I/2qP3ixlNN..JU2dXSN0umG7/Folk3aUaqgSHn/Rl0G82O9WYk66"
              2, "other-member", 0, 0, None, Some "other-member@example.test", None
              3, "administrator", 1, 0, None, Some "administrator@example.test", None
              4, "bot", 2, 0, Some "4-bot-secret", None, None
              5, "inactive-member", 0, 1, None, Some "inactive@example.test", Some "$2a$12$I/2qP3ixlNN..JU2dXSN0umG7/Folk3aUaqgSHn/Rl0G82O9WYk66" ] do
            execute connection
                "INSERT INTO users(id, name, role, status, bot_token, email_address, password_digest, created_at, updated_at) VALUES ($id, $name, $role, $status, $bot, $email, $digest, '2025-01-01 00:00:00', '2025-01-01 00:00:00')"
                [ "$id", box id; "$name", box name; "$role", box role; "$status", box status; "$bot", nullable botToken
                  "$email", nullable email; "$digest", nullable passwordDigest ]
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
        insertSession 15 1 "logout-form-token" (Some "198.51.100.15") (Some "logout-agent") "2026-01-01 12:00:00"
        insertSession 16 1 "logout-query-token" (Some "198.51.100.16") (Some "logout-agent") "2026-01-01 12:00:00"
        insertSession 17 1 "logout-failure-token" (Some "198.51.100.17") (Some "logout-agent") "2026-01-01 12:00:00"
        insertSession 18 1 "logout-blank-token" (Some "198.51.100.18") (Some "logout-agent") "2026-01-01 12:00:00"

        for id, userId, endpoint in
            [ 1, 1, logoutFormEndpoint
              2, 2, logoutFormEndpoint
              3, 1, logoutQueryEndpoint
              4, 1, logoutFailureEndpoint ] do
            execute connection
                "INSERT INTO push_subscriptions(id, user_id, endpoint, p256dh_key, auth_key, created_at, updated_at) VALUES ($id, $user, $endpoint, 'test-p256dh-key', 'test-auth-key', '2025-01-01 00:00:00', '2025-01-01 00:00:00')"
                [ "$id", box id; "$user", box userId; "$endpoint", box endpoint ]
            |> ignore

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
              "push_subscriptions", "id"
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
            CampfireHost.createProviderWithGuidSource databasePath clock (fun () -> Guid.NewGuid()) (readCookieSecret ())
            |> expectOk "open existing message/room SQLite provider"
        let authentication =
            CampfireAuthenticationHost.createProvider databasePath
            |> expectOk "open existing authentication SQLite provider"
        let cookieProvider = CookieHost.createProvider cookies
        let browser = BrowserHost.createProvider()
        let login = LoginHost.createProvider()
        let combined = HostComposition.provider [ campfire; authentication; cookieProvider; browser; login ]
        let probe = ProviderProbe(combined)
        let manifest =
            HostComposition.manifest
                [ CookieHost.manifest; CampfireHost.manifest; CampfireAuthenticationHost.manifest; BrowserHost.manifest; LoginHost.manifest ]
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

    /// Rails' CSRF work on a room GET: the session secret, the page's meta and
    /// composer tokens, then one boost-token call per rendered message.
    let private csrfTrace (body: string) =
        [ "campfire.csrf.session-secret"; "campfire.csrf.form-token"; "campfire.csrf.form-token" ]
        @ List.replicate (body.Split(" data-message-id=\"", StringSplitOptions.None).Length - 1) "campfire.csrf.boost-tokens"

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

    let private assertCookieExpired (exchange: HttpExchange) cookieName =
        let header = exchange.SetCookies |> List.find (fun item -> item.StartsWith(cookieName + "=", StringComparison.Ordinal))
        let pair = header.Split(';', 2)[0]
        equal (cookieName + "=") pair $"{cookieName} cookie is cleared with an empty value"
        check (header.Contains("Max-Age=0", StringComparison.OrdinalIgnoreCase)) $"{cookieName} cookie has Max-Age=0"
        check
            (header.Contains("Expires=Thu, 01 Jan 1970 00:00:00 GMT", StringComparison.OrdinalIgnoreCase))
            $"{cookieName} cookie expires at the epoch"
        check
            (header.Split(';') |> Array.exists (fun part -> part.Trim().Equals("Path=/", StringComparison.OrdinalIgnoreCase)))
            $"{cookieName} cookie clears the root path"
        assertCookieHasFlag exchange cookieName "SameSite=Lax"

    let private sourceHashes () =
        let files =
            [ "tests/AgentLang.Campfire.Http.Tests/AgentLang.Campfire.Http.Tests.fsproj"
              "tests/AgentLang.Campfire.Http.Tests/Program.fs"
              "tests/AgentLang.Campfire.Cookies.Tests/Fixtures/rails-cookie-vectors.json"
              "applications/campfire/http-types.flow"
              "applications/campfire/http.flow"
              "applications/campfire/http-tests.flow"
              "applications/campfire/http-login.flow"
              "applications/campfire/http-login-tests.flow"
              "applications/campfire/sign-out.flow"
              "applications/campfire/sign-out-tests.flow"
              "applications/campfire/http-logout-tests.flow"
              "applications/campfire/http-room-read-tests.flow"
              "applications/campfire/browser-types.flow"
              "applications/campfire/Http/Application.fs"
              "applications/campfire/Http/CookieHost.fs"
              "applications/campfire/Http/ResponseRenderer.fs"
              "applications/campfire/Http/Transport.fs"
              "applications/campfire/Http/LoginHost.fs"
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

    let private checkHttpBehavior (client: HttpClient) (cookies: CookieService) (probe: ProviderProbe) (clock: FixedClock) =
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
        // Report 268: the Rails layout (applications/campfire/layout.flow) titles the page.
        check (room.Body.Contains("<title>Main room</title>", StringComparison.Ordinal)) "GET contains the authorized room title"
        check (room.Body.Contains("<main id=\"main-content\">", StringComparison.Ordinal)) "GET renders the Rails layout"
        equal 40 (room.Body.Split(" data-message-id=\"", StringSplitOptions.None).Length - 1) "GET renders exactly forty messages"
        equal 40 (room.Body.Split("<div id=\"presentation_message_", StringSplitOptions.None).Length - 1) "GET renders forty message presentations"
        check (room.Body.Contains("data-message-id=\"6\"", StringComparison.Ordinal)) "GET contains the oldest row in the newest forty"
        check (room.Body.Contains("data-message-id=\"45\"", StringComparison.Ordinal)) "GET contains the newest row"
        check (not (room.Body.Contains("data-message-id=\"5\"", StringComparison.Ordinal))) "GET excludes rows older than the newest forty"
        check (room.Body.Contains("&lt;script&gt;", StringComparison.Ordinal)) "stored markup is HTML-escaped"
        check (not (room.Body.Contains("<script>", StringComparison.Ordinal))) "stored markup is not emitted as executable markup"
        expectTrace probe <|
            [ "campfire.cookies.verify-session-token"; "campfire.sessions.find-by-token"; "campfire.users.find-by-id"
              "campfire.browser.version-at-least?"; "campfire.rooms.find-for-user"; "campfire.membership.exists?"; "campfire.messages.last-page-with-creators" ] @ csrfTrace room.Body
        check (room.SetCookies |> List.exists (fun value -> value.StartsWith("last_room=", StringComparison.Ordinal))) "first successful room visit writes the last-room cookie"

        // Report 271: Rails' CSRF model. A session without _csrf_token gets one,
        // and every page token unmasks to HMAC-SHA256(secret, identifier).
        currentCheck <- "room page renders Rails CSRF tokens for the session's secret"
        let csrfState =
            getCookie room "_campfire_session"
            |> Option.bind (fun value -> cookies.DecryptRailsSession(value, clock.CurrentInstant))
            |> Option.defaultWith (fun () -> failwith "a session without a CSRF secret must receive one in its Rails session cookie")
        let csrfSecret = csrfState["_csrf_token"].GetValue<string>()
        let fromUrlSafe (text: string) =
            let padded = text.Replace('-', '+').Replace('_', '/')
            Convert.FromBase64String(padded + String('=', (4 - padded.Length % 4) % 4))
        let rawSecret = fromUrlSafe csrfSecret
        equal 32 rawSecret.Length "the generated CSRF secret is 32 random bytes"
        let unmasks (token: string) (identifier: string) =
            let masked = fromUrlSafe token
            masked.Length = 64
            && (Array.map2 (^^^) masked[0..31] masked[32..63]) = HMACSHA256.HashData(rawSecret, Encoding.UTF8.GetBytes identifier)
        let meta = Regex.Match(room.Body, "<meta name=\"csrf-token\" content=\"([^\"]+)\"").Groups[1].Value
        check (unmasks meta "!real_csrf_token") "the csrf meta tag carries the masked global token"
        let forms = Regex.Matches(room.Body, "action=\"(/messages/\d+/boosts)\"[^>]*method=\"post\"><input type=\"hidden\" name=\"authenticity_token\" value=\"([^\"]+)\"")
        equal 320 forms.Count "forty messages render eight quick-boost forms each"
        equal 320 (forms |> Seq.filter (fun (m: Match) -> unmasks m.Groups[2].Value (m.Groups[1].Value + "#post")) |> Seq.length) "every boost form token unmasks to its per-form HMAC"
        equal 320 (forms |> Seq.map (fun (m: Match) -> m.Groups[2].Value) |> Set.ofSeq |> Set.count) "every form gets its own random mask"

        currentCheck <- "unauthenticated room redirect preserves encrypted existing state and return URL"
        let initialState = JsonObject()
        initialState["retained-marker"] <- JsonValue.Create("kept-across-login-redirect")
        let encryptedState = cookies.EncryptRailsSession(initialState, clock.CurrentInstant) |> expectOk "encrypt synthetic starting Rails session"
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
            |> Option.bind (fun value -> cookies.DecryptRailsSession(value, clock.CurrentInstant))
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
        equal (clock.CurrentInstant.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)) (scalarString databasePath "SELECT last_active_at FROM sessions WHERE id = 11" []) "due session refresh commits before CSRF rejection"
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
        equal (clock.CurrentInstant.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)) (scalarString databasePath "SELECT last_active_at FROM sessions WHERE id = 14" []) "orphan session refresh is durable before the missing user result"
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

    let private formPair (name: string) (value: string) = Uri.EscapeDataString(name) + "=" + Uri.EscapeDataString(value)

    let private loginBody email password =
        [ email |> Option.map (formPair "email_address")
          password |> Option.map (formPair "password") ]
        |> List.choose id
        |> String.concat "&"

    let private loginPost path email password accept =
        { (baseSpec "POST" path) with
            ContentType = Some "application/x-www-form-urlencoded"
            Body = Some(loginBody email password)
            Accept = accept
            Origin = Some(publicUri.GetLeftPart(UriPartial.Authority))
            FetchSite = Some "same-origin" }

    let private checkLoginHttpBehavior (client: HttpClient) (cookies: CookieService) (probe: ProviderProbe) (clock: FixedClock) =
        let secretPassword = "secret123456"
        let passwordDigest = "$2a$12$I/2qP3ixlNN..JU2dXSN0umG7/Folk3aUaqgSHn/Rl0G82O9WYk66"
        let memberEmail = "member@example.test"
        let goodToken = createSignedCookie cookies "fresh-token"
        let browserAndUsers = [ "campfire.browser.version-at-least?"; "campfire.users.any?" ]
        let loginAttempt =
            [ "campfire.bans.ip-banned?"
              "campfire.browser.version-at-least?"
              "campfire.sessions.login-attempt-count" ]
        let loginCredentialCheck = loginAttempt @ [ "campfire.users.authenticate-password" ]
        let loginSuccessTrace = loginCredentialCheck @ [ "campfire.sessions.start" ]
        let emailInQuery = "<member&x@example.test>"
        let encryptedExistingState =
            let state = JsonObject()
            state["retained-marker"] <- JsonValue.Create("keep-on-login-page")
            cookies.EncryptRailsSession(state, clock.CurrentInstant)
            |> expectOk "encrypt existing session state for public login GET"

        currentCheck <- "GET and HEAD login page encode query email and ignore signed session and bot cookies"
        probe.Clear()
        let page =
            send client
                { (baseSpec "GET" ("/session/new?email_address=" + Uri.EscapeDataString emailInQuery + "&bot_key=4-bot-secret")) with
                    Cookies = [ "session_token", goodToken; "_campfire_session", encryptedExistingState ]
                    Accept = Some "text/html" }
        expectStatus 200 page
        check (page.ContentType |> Option.exists (fun value -> value.StartsWith("text/html", StringComparison.OrdinalIgnoreCase))) "login GET renders HTML"
        check (page.Body.Contains("value=\"&lt;member&amp;x@example.test&gt;\"", StringComparison.Ordinal)) "login page HTML-encodes the email query value"
        check (not (page.Body.Contains("<member&x@example.test>", StringComparison.Ordinal))) "raw email markup is not reflected"
        check (not (page.Body.Contains(secretPassword, StringComparison.Ordinal))) "login page never includes a password value"
        check (not (page.Body.Contains(passwordDigest, StringComparison.Ordinal))) "login page never includes a password digest"
        expectTrace probe browserAndUsers

        probe.Clear()
        let head =
            send client
                { (baseSpec "HEAD" "/session/new") with
                    Cookies = [ "session_token", goodToken ]
                    Accept = Some "text/html" }
        expectStatus 200 head
        check (head.Body = "") "HEAD login response has no body"
        expectTrace probe browserAndUsers

        currentCheck <- "public login ignores stale duplicate authentication cookies but not ambiguous encrypted state"
        probe.Clear()
        let ignoredAuthCookies = send client { (baseSpec "GET" "/session/new") with CookieHeaderOverride = Some "session_token=%invalid; session_token=stale; last_room=%invalid; last_room=stale" }
        expectStatus 200 ignoredAuthCookies
        expectTrace probe browserAndUsers
        probe.Clear()
        let duplicateState = send client { (baseSpec "GET" "/session/new") with CookieHeaderOverride = Some "_campfire_session=first; _campfire_session=second" }
        expectStatus 400 duplicateState
        expectTrace probe []

        currentCheck <- "login HTML negotiation happens after the browser and users checks"
        probe.Clear()
        let jsonPage = send client { (baseSpec "GET" "/session/new") with Accept = Some "application/json" }
        expectStatus 406 jsonPage
        expectTrace probe browserAndUsers

        probe.Clear()
        let zeroQualityHtml =
            send client { (baseSpec "GET" "/session/new") with Accept = Some "text/html;q=0" }
        expectStatus 200 zeroQualityHtml
        check (zeroQualityHtml.ContentType |> Option.exists (fun value -> value.StartsWith("text/html", StringComparison.OrdinalIgnoreCase))) "q=0 HTML remains a recognized login representation"
        expectTrace probe browserAndUsers

        currentCheck <- "old browser blocks login before users or attempt counter"
        let oldBrowser = Some "Mozilla/5.0 (compatible; MSIE 10.0; Windows NT 6.1; Trident/6.0)"
        probe.Clear()
        let oldPage =
            send client
                { (baseSpec "GET" "/session/new") with
                    UserAgent = oldBrowser
                    Accept = Some "text/html" }
        expectStatus 200 oldPage
        check (oldPage.Body.Contains("Please upgrade your browser.", StringComparison.Ordinal)) "old browser receives the fixed compatibility page"
        expectTrace probe []

        probe.Clear()
        let oldPost =
            send client
                { (loginPost "/session" (Some memberEmail) (Some secretPassword) None) with
                    UserAgent = oldBrowser }
        expectStatus 200 oldPost
        check (oldPost.Body.Contains("Please upgrade your browser.", StringComparison.Ordinal)) "old browser POST is gated before credentials"
        expectTrace probe [ "campfire.bans.ip-banned?" ]

        currentCheck <- "login CSRF failure stops before browser checks and attempt or password operations"
        probe.Clear()
        let csrf =
            send client
                { (loginPost "/session" (Some memberEmail) (Some secretPassword) None) with
                    Origin = Some "https://attacker.invalid"
                    FetchSite = Some "cross-site" }
        expectStatus 422 csrf
        expectTrace probe [ "campfire.bans.ip-banned?" ]

        currentCheck <- "ambiguous encrypted session cookies are rejected before login dispatch"
        probe.Clear()
        let duplicateSignedCookie = cookiePair "_campfire_session" goodToken + "; " + cookiePair "_campfire_session" goodToken
        let malformedCookie =
            send client
                { (baseSpec "POST" "/session") with
                    CookieHeaderOverride = Some duplicateSignedCookie
                    ContentType = Some "application/x-www-form-urlencoded"
                    Body = Some(loginBody (Some memberEmail) (Some secretPassword)) }
        expectStatus 400 malformedCookie
        expectTrace probe []

        currentCheck <- "malformed and unsupported login forms stay bounded and ordered"
        probe.Clear()
        let duplicateField =
            send client
                { (loginPost "/session" None None None) with
                    Body = Some(formPair "email_address" memberEmail + "&" + formPair "email_address" "other@example.test" + "&" + formPair "password" secretPassword) }
        expectStatus 400 duplicateField
        expectTrace probe loginAttempt

        probe.Clear()
        let unsupportedMedia =
            send client
                { (loginPost "/session" (Some memberEmail) (Some secretPassword) None) with
                    ContentType = Some "text/plain" }
        expectStatus 415 unsupportedMedia
        expectTrace probe loginAttempt

        probe.Clear()
        let unsupportedJsonMedia =
            send client
                { (loginPost "/session" (Some memberEmail) (Some secretPassword) None) with
                    ContentType = Some "application/json" }
        expectStatus 415 unsupportedJsonMedia
        expectTrace probe loginAttempt

        let tooLarge = String('x', int Transport.MaximumBodyBytes + 1)
        probe.Clear()
        let oversizedChunked =
            send client
                { (loginPost "/session" None None None) with
                    Body = Some tooLarge
                    Chunked = true }
        expectStatus 413 oversizedChunked
        expectTrace probe []

        probe.Clear()
        let oversizedHeaders =
            send client
                { (baseSpec "POST" "/session") with
                    ExtraHeaders = [ "X-Test-Large", String('h', 33000) ] }
        expectStatus 431 oversizedHeaders
        expectTrace probe []

        currentCheck <- "login requests with missing, wrong, or inactive credentials do not start sessions"
        let missingCredentials =
            [ None, None
              Some memberEmail, None
              None, Some secretPassword ]
        for email, password in missingCredentials do
            probe.Clear()
            let missing = send client (loginPost "/session" email password None)
            expectStatus 401 missing
            check (not (missing.Body.Contains(secretPassword, StringComparison.Ordinal))) "missing-credential response never reflects the password"
            expectTrace probe loginAttempt

        probe.Clear()
        let wrongPassword = "wrong-password-should-not-leak"
        let sessionsBeforeInvalidCredentials = scalarLong databasePath "SELECT COUNT(*) FROM sessions" []
        let wrong = send client (loginPost "/session" (Some memberEmail) (Some wrongPassword) None)
        expectStatus 401 wrong
        check (not (wrong.Body.Contains(wrongPassword, StringComparison.Ordinal))) "wrong password is not reflected in the response"
        check (not (wrong.Body.Contains(passwordDigest, StringComparison.Ordinal))) "password digest is not reflected in the response"
        equal
            [ StringValue "<redacted:string>"; StringValue "<redacted:string>" ]
            (probe.Calls |> List.find (fun (name, _) -> name = "campfire.users.authenticate-password") |> snd)
            "provider probe records only the credential value types"
        expectTrace probe loginCredentialCheck

        probe.Clear()
        let inactive = send client (loginPost "/session" (Some "inactive@example.test") (Some secretPassword) None)
        expectStatus 401 inactive
        check (not (inactive.Body.Contains(secretPassword, StringComparison.Ordinal))) "inactive-account response never reflects the password"
        expectTrace probe loginCredentialCheck

        probe.Clear()
        let jsonFailure =
            send client (loginPost "/session" (Some memberEmail) (Some wrongPassword) (Some "application/json"))
        expectStatus 406 jsonFailure
        check (jsonFailure.Body = "") "credential failure negotiated away from HTML has an empty body"
        equal sessionsBeforeInvalidCredentials (scalarLong databasePath "SELECT COUNT(*) FROM sessions" []) "missing, wrong, inactive, and non-HTML failures create no session"
        expectTrace probe loginCredentialCheck

        currentCheck <- "successful login redirects despite JSON Accept and signs a permanent HttpOnly Lax cookie"
        clock.Advance(TimeSpan.FromSeconds 180.0)
        let sessionsBefore = scalarLong databasePath "SELECT COUNT(*) FROM sessions" []
        probe.Clear()
        let success =
            send client
                { (loginPost "/session?bot_key=4-bot-secret" (Some memberEmail) (Some secretPassword) (Some "application/json")) with
                    Cookies = [ "session_token", goodToken ] }
        expectStatus 302 success
        equal (Some "/") success.Location "login without a return target falls back to the configured root"
        equal (sessionsBefore + 1L) (scalarLong databasePath "SELECT COUNT(*) FROM sessions" []) "successful login creates exactly one session"
        let issuedCookie =
            getCookie success "session_token"
            |> Option.defaultWith (fun () -> failwith "successful login did not issue a signed session cookie")
        let issuedToken =
            cookies.VerifySessionToken(issuedCookie, clock.CurrentInstant)
            |> Option.defaultWith (fun () -> failwith "successful login session cookie failed signature verification")
        equal issuedToken (scalarString databasePath "SELECT token FROM sessions ORDER BY id DESC LIMIT 1" []) "issued token matches the persisted session"
        assertCookieHasFlag success "session_token" "HttpOnly"
        assertCookieHasFlag success "session_token" "SameSite=Lax"
        assertCookieHasFlag success "session_token" "Path=/"
        let sessionHeader = success.SetCookies |> List.find (fun value -> value.StartsWith("session_token=", StringComparison.Ordinal))
        check (sessionHeader.Contains("Expires=", StringComparison.OrdinalIgnoreCase)) "session token cookie is permanent"
        check (not (success.Body.Contains(secretPassword, StringComparison.Ordinal))) "successful login response never includes the password"
        check (not (success.Body.Contains(passwordDigest, StringComparison.Ordinal))) "successful login response never includes the password digest"
        expectTrace probe loginSuccessTrace

        currentCheck <- "login failure preserves encrypted return target for a later successful attempt"
        let returnTarget = "http://campfire.test/rooms/1?view=compact&source=login"
        let returnState = JsonObject()
        returnState["return_to_after_authenticating"] <- JsonValue.Create returnTarget
        returnState["retained-marker"] <- JsonValue.Create "preserved-through-login"
        let returnCookie = cookies.EncryptRailsSession(returnState, clock.CurrentInstant) |> expectOk "encrypt login return target"
        probe.Clear()
        let failedReturn =
            send client
                { (loginPost "/session" (Some memberEmail) (Some wrongPassword) None) with
                    Cookies = [ "_campfire_session", returnCookie ] }
        expectStatus 401 failedReturn
        check (not (failedReturn.SetCookies |> List.exists (fun value -> value.StartsWith("_campfire_session=", StringComparison.Ordinal)))) "failed login does not replace the encrypted return target"
        check (not (failedReturn.Body.Contains(wrongPassword, StringComparison.Ordinal))) "failed login response does not echo its password"
        expectTrace probe loginCredentialCheck

        probe.Clear()
        let returned =
            send client
                { (loginPost "/session" (Some memberEmail) (Some secretPassword) None) with
                    Cookies = [ "_campfire_session", returnCookie ] }
        expectStatus 302 returned
        equal (Some "/rooms/1?view=compact&source=login") returned.Location "same-authority return target is used after success"
        let consumedState =
            getCookie returned "_campfire_session"
            |> Option.bind (fun value -> cookies.DecryptRailsSession(value, clock.CurrentInstant))
            |> Option.defaultWith (fun () -> failwith "successful return-target login did not produce decryptable state")
        check (isNull consumedState["return_to_after_authenticating"]) "successful login consumes the return target"
        equal "preserved-through-login" (consumedState["retained-marker"].GetValue<string>()) "successful login preserves unrelated encrypted state"
        expectTrace probe loginSuccessTrace

        currentCheck <- "hostile encrypted return targets always fall back to the configured root"
        let hostileTargets =
            [ "https://attacker.invalid/steal"
              "//attacker.invalid/steal"
              "\\\\attacker.invalid\\share"
              "http://campfire.test/rooms/1\r\nLocation: https://attacker.invalid/steal" ]
        for target in hostileTargets do
            let state = JsonObject()
            state["return_to_after_authenticating"] <- JsonValue.Create target
            state["retained-marker"] <- JsonValue.Create "kept"
            let encrypted = cookies.EncryptRailsSession(state, clock.CurrentInstant) |> expectOk "encrypt hostile test return target"
            probe.Clear()
            let response =
                send client
                    { (loginPost "/session" (Some memberEmail) (Some secretPassword) None) with
                        Cookies = [ "_campfire_session", encrypted ] }
            expectStatus 302 response
            equal (Some "/") response.Location $"hostile return target {target} is rejected"
            let savedState =
                getCookie response "_campfire_session"
                |> Option.bind (fun value -> cookies.DecryptRailsSession(value, clock.CurrentInstant))
                |> Option.defaultWith (fun () -> failwith "hostile return-target login did not preserve encrypted state")
            check (isNull savedState["return_to_after_authenticating"]) "hostile return target is consumed"
            equal "kept" (savedState["retained-marker"].GetValue<string>()) "hostile target handling preserves unrelated state"
            expectTrace probe loginSuccessTrace

        currentCheck <- "non-string encrypted return target falls back to root and is consumed"
        let nonStringState = JsonObject()
        nonStringState["return_to_after_authenticating"] <- JsonValue.Create(42)
        nonStringState["retained-marker"] <- JsonValue.Create "kept-after-invalid-return"
        let nonStringCookie =
            cookies.EncryptRailsSession(nonStringState, clock.CurrentInstant)
            |> expectOk "encrypt non-string login return target"
        probe.Clear()
        let nonStringReturn =
            send client
                { (loginPost "/session" (Some memberEmail) (Some secretPassword) None) with
                    Cookies = [ "_campfire_session", nonStringCookie ] }
        expectStatus 302 nonStringReturn
        equal (Some "/") nonStringReturn.Location "non-string return target falls back to root"
        let nonStringSavedState =
            getCookie nonStringReturn "_campfire_session"
            |> Option.bind (fun value -> cookies.DecryptRailsSession(value, clock.CurrentInstant))
            |> Option.defaultWith (fun () -> failwith "non-string return-target login did not preserve encrypted state")
        check (isNull nonStringSavedState["return_to_after_authenticating"]) "non-string return target is consumed"
        equal "kept-after-invalid-return" (nonStringSavedState["retained-marker"].GetValue<string>()) "non-string target handling preserves unrelated state"
        expectTrace probe loginSuccessTrace

        let persistedTrace = File.ReadAllText operationTracePath
        let persistedTranscript = File.ReadAllText transcriptPath
        for sensitive in [ secretPassword; passwordDigest; wrongPassword ] do
            check (not (persistedTrace.Contains(sensitive, StringComparison.Ordinal))) "provider trace never stores password material"
            check (not (persistedTranscript.Contains(sensitive, StringComparison.Ordinal))) "HTTP transcript never stores password material"
        issuedCookie

    let private checkLoginRateBoundary (client: HttpClient) (probe: ProviderProbe) (clock: FixedClock) =
        currentCheck <- "login attempt budget allows ten posts and rejects the eleventh"
        let loginAttempt =
            [ "campfire.bans.ip-banned?"
              "campfire.browser.version-at-least?"
              "campfire.sessions.login-attempt-count" ]
        clock.Advance(TimeSpan.FromSeconds 180.0)
        for _ in 1 .. 10 do
            probe.Clear()
            let allowed = send client (loginPost "/session" None None None)
            expectStatus 401 allowed
            expectTrace probe loginAttempt
        let sessionsBeforeLimit = scalarLong databasePath "SELECT COUNT(*) FROM sessions" []
        probe.Clear()
        let limited = send client (loginPost "/session" (Some "member@example.test") (Some "wrong-after-limit") None)
        expectStatus 429 limited
        check (not (limited.Body.Contains("wrong-after-limit", StringComparison.Ordinal))) "rate-limited response does not echo credentials"
        equal sessionsBeforeLimit (scalarLong databasePath "SELECT COUNT(*) FROM sessions" []) "eleventh login attempt creates no session"
        expectTrace probe loginAttempt

        currentCheck <- "login attempt budget expires exactly at 180 seconds"
        clock.Advance(TimeSpan.FromSeconds 180.0)
        probe.Clear()
        let expired = send client (loginPost "/session" None None None)
        expectStatus 401 expired
        expectTrace probe loginAttempt

    let private checkLogoutHttpBehavior (client: HttpClient) (cookies: CookieService) (probe: ProviderProbe) =
        let token name = createSignedCookie cookies name
        let form endpoint = formPair "push_subscription_endpoint" endpoint
        let validGateTrace =
            [ "campfire.bans.ip-banned?"
              "campfire.cookies.verify-session-token"
              "campfire.sessions.find-by-token"
              "campfire.users.find-by-id"
              "campfire.browser.version-at-least?" ]
        let revocationTrace endpoint = validGateTrace @ [ endpoint; "campfire.sessions.destroy" ]
        let signedOut response =
            expectStatus 302 response
            equal (Some "/") response.Location "successful logout redirects to the fixed root"
            equal "" response.Body "successful logout has an empty response body"
            equal 2 response.SetCookies.Length "successful logout emits exactly two cookie directives"
            assertCookieExpired response "session_token"
            assertCookieExpired response "_campfire_session"
            check (response.SetCookies |> List.forall (fun value -> not (value.StartsWith("last_room=", StringComparison.Ordinal)))) "logout leaves last_room untouched"

        let sessionExists sessionToken =
            scalarLong databasePath "SELECT COUNT(*) FROM sessions WHERE token = $token" [ "$token", box sessionToken ]

        let subscriptionCount userId endpoint =
            scalarLong databasePath "SELECT COUNT(*) FROM push_subscriptions WHERE user_id = $user AND endpoint = $endpoint"
                [ "$user", box userId; "$endpoint", box endpoint ]

        let assertFixtureUnchanged label =
            equal 1L (sessionExists "logout-form-token") $"{label} preserves the form logout session"
            equal 1L (sessionExists "logout-query-token") $"{label} preserves the query logout session"
            equal 1L (sessionExists "logout-failure-token") $"{label} preserves the failure logout session"
            equal 1L (sessionExists "logout-blank-token") $"{label} preserves the blank logout session"
            equal 4L (scalarLong databasePath "SELECT COUNT(*) FROM push_subscriptions" []) $"{label} preserves all push subscriptions"
            equal 1L (subscriptionCount 1 logoutFormEndpoint) $"{label} preserves the member's matching endpoint"
            equal 1L (subscriptionCount 2 logoutFormEndpoint) $"{label} preserves another user's identical endpoint"
            equal 1L (subscriptionCount 1 logoutQueryEndpoint) $"{label} preserves the member's query endpoint"
            equal 1L (subscriptionCount 1 logoutFailureEndpoint) $"{label} preserves the failure endpoint"

        let request tokenName path contentType body origin fetchSite userAgent cookiesForRequest =
            { (baseSpec "DELETE" path) with
                Cookies = [ "session_token", token tokenName ] @ cookiesForRequest
                ContentType = contentType
                Body = body
                Origin = origin
                FetchSite = fetchSite
                UserAgent = userAgent }

        let formRequest tokenName endpoint =
            request tokenName "/session" (Some "application/x-www-form-urlencoded") (Some(form endpoint))
                (Some(publicUri.GetLeftPart(UriPartial.Authority))) (Some "same-origin") (Some goodAgent) []

        let checkGateRejection label expectedStatus spec expectedTrace =
            currentCheck <- label
            probe.Clear()
            let response = send client spec
            expectStatus expectedStatus response
            expectTrace probe expectedTrace
            assertFixtureUnchanged label
            response

        let formSessionCookie = token "logout-form-token"
        let initialState = JsonObject()
        initialState["retained-marker"] <- JsonValue.Create("must-not-survive-logout")
        initialState["return_to_after_authenticating"] <- JsonValue.Create("https://campfire.test/private?after=logout")
        initialState["flash"] <- JsonObject()
        let encryptedState = cookies.EncryptRailsSession(initialState, now) |> expectOk "encrypt logout starting Rails session"

        currentCheck <- "unauthenticated DELETE cannot revoke a session or push endpoint"
        probe.Clear()
        let unauthenticated =
            send client
                { (formRequest "logout-form-token" logoutFormEndpoint) with
                    Cookies = [] }
        expectStatus 302 unauthenticated
        equal (Some "/session/new") unauthenticated.Location "unauthenticated logout follows the login redirect"
        expectTrace probe [ "campfire.bans.ip-banned?" ]
        assertFixtureUnchanged "unauthenticated logout"

        let forged =
            checkGateRejection
                "forged logout stops after authentication and before browser or provider deletion"
                422
                { (formRequest "logout-form-token" logoutFormEndpoint) with
                    Cookies = [ "session_token", formSessionCookie ]
                    Origin = Some "https://attacker.invalid"
                    FetchSite = Some "cross-site" }
                [ "campfire.bans.ip-banned?"; "campfire.cookies.verify-session-token"; "campfire.sessions.find-by-token"; "campfire.users.find-by-id" ]
        equal "" forged.Body "forged logout response is empty"

        let oldBrowser = Some "Mozilla/5.0 (compatible; MSIE 10.0; Windows NT 6.1; Trident/6.0)"
        let incompatible =
            checkGateRejection
                "old browser logout stops before push and session deletion"
                200
                { (formRequest "logout-form-token" logoutFormEndpoint) with
                    Cookies = [ "session_token", formSessionCookie ]
                    UserAgent = oldBrowser }
                [ "campfire.bans.ip-banned?"; "campfire.cookies.verify-session-token"; "campfire.sessions.find-by-token";
                  "campfire.users.find-by-id" ]
        check (incompatible.Body.Contains("Please upgrade your browser.", StringComparison.Ordinal)) "old browser logout returns the fixed compatibility page"

        let duplicateForm =
            request "logout-form-token" "/session" (Some "application/x-www-form-urlencoded")
                (Some(form logoutFormEndpoint + "&" + form logoutQueryEndpoint))
                (Some(publicUri.GetLeftPart(UriPartial.Authority))) (Some "same-origin") (Some goodAgent) []
        checkGateRejection
            "duplicate logout form fields fail without mutation"
            400
            duplicateForm
            (validGateTrace)
        |> ignore

        let duplicateQueryPath =
            "/session?push_subscription_endpoint=" + Uri.EscapeDataString logoutFormEndpoint
            + "&push_subscription_endpoint=" + Uri.EscapeDataString logoutQueryEndpoint
        let duplicateQuery =
            request "logout-form-token" duplicateQueryPath None None
                (Some(publicUri.GetLeftPart(UriPartial.Authority))) (Some "same-origin") (Some goodAgent) []
        checkGateRejection
            "duplicate logout query fields fail without mutation"
            400
            duplicateQuery
            validGateTrace
        |> ignore

        let unsupportedMedia =
            request "logout-form-token" "/session" (Some "application/json") (Some(form logoutFormEndpoint))
                (Some(publicUri.GetLeftPart(UriPartial.Authority))) (Some "same-origin") (Some goodAgent) []
        checkGateRejection
            "unsupported logout form media fails without mutation"
            415
            unsupportedMedia
            validGateTrace
        |> ignore

        let tooLargeBody = String('x', int Transport.MaximumBodyBytes + 1)
        let oversized =
            request "logout-form-token" "/session" (Some "application/x-www-form-urlencoded") (Some tooLargeBody)
                (Some(publicUri.GetLeftPart(UriPartial.Authority))) (Some "same-origin") (Some goodAgent) []
        currentCheck <- "oversized logout form is rejected before provider dispatch"
        probe.Clear()
        let oversizedResponse = send client oversized
        expectStatus 413 oversizedResponse
        expectTrace probe []
        assertFixtureUnchanged currentCheck

        let chunkedOversized =
            { oversized with Chunked = true }
        currentCheck <- "chunked oversized logout form is bounded before provider dispatch"
        probe.Clear()
        let chunkedResponse = send client chunkedOversized
        expectStatus 413 chunkedResponse
        expectTrace probe []
        assertFixtureUnchanged currentCheck

        currentCheck <- "push deletion failure retains the authenticated session and emits no sign-out cookies"
        probe.Clear()
        let failedPush = send client (formRequest "logout-failure-token" logoutFailureEndpoint)
        expectStatus 500 failedPush
        equal "" failedPush.Body "push provider failure returns an empty response"
        check failedPush.SetCookies.IsEmpty "push provider failure emits no cookie directive"
        equal 1L (sessionExists "logout-failure-token") "push provider failure preserves the session"
        equal 1L (subscriptionCount 1 logoutFailureEndpoint) "push provider failure preserves the push row"
        expectTrace probe (validGateTrace @ [ "campfire.push-subscriptions.destroy-for-user" ])

        currentCheck <- "valid form logout overrides duplicate query endpoints and deletes only this user's matching endpoint"
        probe.Clear()
        let loggedOut =
            send client
                { (formRequest "logout-form-token" logoutFormEndpoint) with
                    Path = duplicateQueryPath
                    Cookies =
                        [ "session_token", formSessionCookie
                          "_campfire_session", encryptedState
                          "last_room", "42" ] }
        signedOut loggedOut
        expectTrace probe (revocationTrace "campfire.push-subscriptions.destroy-for-user")
        equal 0L (sessionExists "logout-form-token") "successful logout destroys the persisted current session"
        equal 0L (subscriptionCount 1 logoutFormEndpoint) "logout removes the requesting user's matching push endpoint"
        equal 1L (subscriptionCount 2 logoutFormEndpoint) "logout leaves another user's identical endpoint intact"
        equal 1L (subscriptionCount 1 logoutQueryEndpoint) "logout leaves the requesting user's unrelated endpoint intact"
        equal 1L (subscriptionCount 1 logoutFailureEndpoint) "logout leaves the unrequested failure endpoint intact"
        equal 3L (scalarLong databasePath "SELECT COUNT(*) FROM push_subscriptions" []) "form logout deletes only its one matching subscription"
        check (getCookie loggedOut "_campfire_session" = Some "") "logout expires the encrypted state cookie instead of returning prior flash or return state"
        check (getCookie loggedOut "session_token" = Some "") "logout does not refresh the signed session token"

        currentCheck <- "query logout accepts a blank form body and revokes its matching endpoint"
        probe.Clear()
        let queryPath = "/session?push_subscription_endpoint=" + Uri.EscapeDataString logoutQueryEndpoint
        let queryLogout =
            send client
                { (request "logout-query-token" queryPath None None
                    (Some(publicUri.GetLeftPart(UriPartial.Authority))) (Some "same-origin") (Some goodAgent) []) with
                    Cookies = [ "session_token", token "logout-query-token" ] }
        signedOut queryLogout
        expectTrace probe (revocationTrace "campfire.push-subscriptions.destroy-for-user")
        equal 0L (sessionExists "logout-query-token") "query logout destroys the persisted current session"
        equal 0L (subscriptionCount 1 logoutQueryEndpoint) "query logout removes the requesting user's matching endpoint"
        equal 1L (subscriptionCount 2 logoutFormEndpoint) "query logout leaves the other user's same-endpoint subscription intact"
        equal 2L (scalarLong databasePath "SELECT COUNT(*) FROM push_subscriptions" []) "query logout deletes only its one matching subscription"

        currentCheck <- "blank logout without a push endpoint destroys only the current session"
        probe.Clear()
        let blankLogout =
            send client
                { (request "logout-blank-token" "/session" None None
                    (Some(publicUri.GetLeftPart(UriPartial.Authority))) (Some "same-origin") (Some goodAgent) []) with
                    Cookies = [ "session_token", token "logout-blank-token" ] }
        signedOut blankLogout
        expectTrace probe (validGateTrace @ [ "campfire.sessions.destroy" ])
        equal 0L (sessionExists "logout-blank-token") "blank logout destroys the persisted current session"
        equal 2L (scalarLong databasePath "SELECT COUNT(*) FROM push_subscriptions" []) "blank logout does not touch any push subscription"
        token "logout-form-token"

    let private checkBannedHttpBehavior (client: HttpClient) (cookies: CookieService) (probe: ProviderProbe) =
        let token name = createSignedCookie cookies name
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

        probe.Clear()
        let bannedLogin =
            send client (loginPost "/session" (Some "member@example.test") (Some "password-should-not-be-checked") None)
        expectStatus 429 bannedLogin
        check (not (bannedLogin.Body.Contains("password-should-not-be-checked", StringComparison.Ordinal))) "banned login response does not echo credentials"
        expectTrace probe [ "campfire.bans.ip-banned?" ]

        let getWhileBanned =
            send client
                { (baseSpec "GET" "/rooms/1") with
                    Cookies = [ "session_token", token "fresh-token" ]
                    FetchSite = Some "same-origin"
                    ExtraHeaders = [ "X-Forwarded-For", "203.0.113.99" ] }
        expectStatus 200 getWhileBanned
        expectTrace probe <|
            [ "campfire.cookies.verify-session-token"; "campfire.sessions.find-by-token"; "campfire.users.find-by-id"
              "campfire.browser.version-at-least?"; "campfire.rooms.find-for-user"; "campfire.membership.exists?"; "campfire.messages.last-page-with-creators" ] @ csrfTrace getWhileBanned.Body

    let private checkEmptyUsersHttpBehavior (sourceDirectory: string) (clock: FixedClock) (cookies: CookieService) =
        currentCheck <- "empty users login redirects to first run"
        let emptyDatabase = Path.Combine(runDirectory, "empty-users.sqlite3")
        let emptyProject = Path.Combine(runDirectory, "empty-users-project")
        seedFixture emptyDatabase
        executeAtPath
            emptyDatabase
            "DROP TRIGGER reject_test_push_subscription_delete; DELETE FROM messages; DELETE FROM action_text_rich_texts; DELETE FROM memberships; DELETE FROM rooms; DELETE FROM sessions; DELETE FROM push_subscriptions; DELETE FROM bans; DELETE FROM users;"
            []
        |> ignore
        let environment, probe, _ = createEnvironment emptyDatabase (clock :> IClock) cookies
        let application = Application.create emptyProject sourceDirectory environment
        let options =
            { PublicBaseUri = publicUri
              ForceSsl = false
              Cookies = cookies
              Clock = fun () -> clock.CurrentInstant
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
            if isNull feature || feature.Addresses.Count = 0 then failwith "empty-users Kestrel did not expose its loopback address"
            use handler = new HttpClientHandler(AllowAutoRedirect = false, UseCookies = false, UseProxy = false)
            use client = new HttpClient(handler)
            client.Timeout <- TimeSpan.FromSeconds(10.0)
            client.BaseAddress <- Uri(feature.Addresses |> Seq.head)
            probe.Clear()
            let firstRun = send client (baseSpec "GET" "/session/new")
            expectStatus 302 firstRun
            equal (Some "/first_run") firstRun.Location "empty users redirect to first run"
            expectTrace probe [ "campfire.browser.version-at-least?"; "campfire.users.any?" ]
        finally
            web.StopAsync().GetAwaiter().GetResult()
            probe.AsProvider.Dispose()

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
              Clock = fun () -> clock.CurrentInstant
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

            checkHttpBehavior client cookies providerProbe clock
            let issuedLoginCookie = checkLoginHttpBehavior client cookies providerProbe clock
            checkLoginRateBoundary client providerProbe clock
            let revokedLogoutCookie = checkLogoutHttpBehavior client cookies providerProbe
            checkBannedHttpBehavior client cookies providerProbe
            checkEmptyUsersHttpBehavior sourceDirectory clock cookies

            currentCheck <- "same dictionary can be reopened without database mutation"
            let before = snapshot databasePath
            let databaseDigestBefore = databaseDigest databasePath
            let currentStore = Path.Combine(projectDirectory, ".agentlang", "store", "CURRENT")
            let dictionaryPointerBefore = File.ReadAllBytes currentStore
            let providerCallsBeforeRestart = providerProbe.Calls.Length
            let reopened = Application.create projectDirectory sourceDirectory environment
            equal before (snapshot databasePath) "restarting the application preserves the asserted domain summary"
            equal databaseDigestBefore (databaseDigest databasePath) "restarting the application leaves every fixture table row unchanged"
            equal providerCallsBeforeRestart providerProbe.Calls.Length "startup/reload does not execute database operations"
            check (dictionaryPointerBefore.AsSpan().SequenceEqual(File.ReadAllBytes currentStore)) "second startup reuses the same persisted dictionary generation"
            let words = reopened.Engine.Dispatch("words", JsonObject())
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

            currentCheck <- "login session restores from SQLite after application restart"
            let restartedEnvironment, restartedProbe, restartedTestFactoryCalls =
                createEnvironment databasePath (clock :> IClock) cookies
            try
                let restarted = Application.create projectDirectory sourceDirectory restartedEnvironment
                equal before (snapshot databasePath) "fresh providers reopen the durable database without changing its domain summary"
                equal databaseDigestBefore (databaseDigest databasePath) "fresh providers preserve every database row on startup"
                equal 0 (restartedTestFactoryCalls()) "fresh production provider startup does not construct a test provider"
                equal 0 restartedProbe.Calls.Length "fresh provider startup performs no database operation"
                let restartOptions = { options with Invoke = restarted.Invoke }
                let restartBuilder = WebApplication.CreateBuilder([||])
                restartBuilder.WebHost.UseUrls("http://127.0.0.1:0") |> ignore
                restartBuilder.Logging.ClearProviders() |> ignore
                use restartWeb = restartBuilder.Build()
                Transport.mapRoutes restartWeb restartOptions
                restartWeb.StartAsync().GetAwaiter().GetResult()
                try
                    let restartServer = restartWeb.Services.GetRequiredService<IServer>()
                    let restartFeature = restartServer.Features.Get<IServerAddressesFeature>()
                    if isNull restartFeature || restartFeature.Addresses.Count = 0 then
                        failwith "restarted Kestrel did not expose its loopback address"
                    use restartHandler = new HttpClientHandler(AllowAutoRedirect = false, UseCookies = false, UseProxy = false)
                    use restartClient = new HttpClient(restartHandler)
                    restartClient.Timeout <- TimeSpan.FromSeconds(10.0)
                    restartClient.BaseAddress <- Uri(restartFeature.Addresses |> Seq.head)
                    restartedProbe.Clear()
                    let restoredRoom =
                        send restartClient
                            { (baseSpec "GET" "/rooms/1") with
                                Cookies = [ "session_token", issuedLoginCookie ] }
                    expectStatus 200 restoredRoom
                    check (restoredRoom.Body.Contains("<title>Main room</title>", StringComparison.Ordinal)) "restarted application restores the persisted login session"
                    expectTrace restartedProbe <|
                        [ "campfire.cookies.verify-session-token"; "campfire.sessions.find-by-token"; "campfire.users.find-by-id"
                          "campfire.browser.version-at-least?"; "campfire.rooms.find-for-user"; "campfire.membership.exists?"
                          "campfire.messages.last-page-with-creators" ] @ csrfTrace restoredRoom.Body

                    currentCheck <- "revoked signed logout token stays rejected after application restart"
                    restartedProbe.Clear()
                    let rejectedRevokedToken =
                        send restartClient
                            { (baseSpec "GET" "/rooms/1") with
                                Cookies = [ "session_token", revokedLogoutCookie ] }
                    expectStatus 302 rejectedRevokedToken
                    equal (Some "/session/new") rejectedRevokedToken.Location "revoked persisted token cannot restore after restart"
                    expectTrace restartedProbe [ "campfire.cookies.verify-session-token"; "campfire.sessions.find-by-token" ]
                finally
                    restartWeb.StopAsync().GetAwaiter().GetResult()
            finally
                restartedProbe.AsProvider.Dispose()
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
