namespace AgentLang.Campfire.Authentication.Persistence.Tests

open System
open System.Globalization
open System.IO
open System.Text.Json
open System.Threading.Tasks
open Microsoft.Data.Sqlite
open AgentLang.Campfire.Persistence
open AgentLang.Campfire.Authentication.Persistence

module Program =
    let mutable private assertions = 0
    let mutable private groups = 0

    let private check condition message =
        assertions <- assertions + 1
        if not condition then failwith message

    let private equal expected actual message =
        assertions <- assertions + 1
        if expected <> actual then
            failwith $"{message}: expected {expected}, got {actual}"

    let private evidenceDirectory =
        Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, "..", "..", ".agentlang", "campfire-auth-storage-239", "databases"))

    let private execute (connection: SqliteConnection) (sql: string) (parameters: (string * obj) list) =
        use command = connection.CreateCommand()
        command.CommandText <- sql
        for name, value in parameters do
            command.Parameters.AddWithValue(name, value) |> ignore
        command.ExecuteNonQuery()

    let private createFixture label seed =
        Directory.CreateDirectory(evidenceDirectory) |> ignore
        let path =
            Path.Combine(evidenceDirectory, $"{label}-{DateTime.UtcNow:yyyyMMddTHHmmssfffZ}-{Guid.NewGuid():N}.db")
        let builder = SqliteConnectionStringBuilder()
        builder.DataSource <- path
        builder.Mode <- SqliteOpenMode.ReadWriteCreate
        builder.ForeignKeys <- true
        builder.Pooling <- false
        use connection = new SqliteConnection(builder.ToString())
        connection.Open()
        use command = connection.CreateCommand()
        command.CommandText <-
            "CREATE TABLE users (id INTEGER PRIMARY KEY, name TEXT NOT NULL, role INTEGER NOT NULL, status INTEGER NOT NULL, bot_token TEXT, email_address TEXT, password_digest TEXT, created_at TEXT NOT NULL, updated_at TEXT NOT NULL); " +
            "CREATE UNIQUE INDEX users_bot_token_unique ON users(bot_token); " +
            "CREATE TABLE sessions (id INTEGER PRIMARY KEY, user_id INTEGER NOT NULL REFERENCES users(id), token TEXT NOT NULL UNIQUE, ip_address TEXT, user_agent TEXT, last_active_at TEXT NOT NULL, created_at TEXT NOT NULL, updated_at TEXT NOT NULL); " +
            "CREATE TABLE push_subscriptions (id INTEGER PRIMARY KEY, auth_key TEXT, created_at TEXT NOT NULL, endpoint TEXT, p256dh_key TEXT, updated_at TEXT NOT NULL, user_agent TEXT, user_id INTEGER NOT NULL REFERENCES users(id)); " +
            "CREATE INDEX push_subscriptions_endpoint_keys_index ON push_subscriptions(endpoint, p256dh_key, auth_key); " +
            "CREATE INDEX push_subscriptions_user_id_index ON push_subscriptions(user_id); " +
            "CREATE TABLE bans (id INTEGER PRIMARY KEY, user_id INTEGER NOT NULL REFERENCES users(id), ip_address TEXT NOT NULL, created_at TEXT NOT NULL, updated_at TEXT NOT NULL); " +
            "CREATE TABLE rooms (id INTEGER PRIMARY KEY, created_at TEXT NOT NULL, creator_id INTEGER NOT NULL, name TEXT, type TEXT NOT NULL, updated_at TEXT NOT NULL); " +
            "CREATE TABLE memberships (id INTEGER PRIMARY KEY, room_id INTEGER NOT NULL, user_id INTEGER NOT NULL, created_at TEXT NOT NULL, updated_at TEXT NOT NULL); " +
            "CREATE INDEX sessions_token_index ON sessions(token); " +
            "CREATE INDEX bans_ip_address_index ON bans(ip_address); " +
            "CREATE UNIQUE INDEX memberships_room_user_unique ON memberships(room_id, user_id);"
        command.ExecuteNonQuery() |> ignore
        seed connection
        path

    let private scalarLong path sql =
        let builder = SqliteConnectionStringBuilder()
        builder.DataSource <- path
        builder.Mode <- SqliteOpenMode.ReadWrite
        builder.ForeignKeys <- true
        builder.Pooling <- false
        use connection = new SqliteConnection(builder.ToString())
        connection.Open()
        use command = connection.CreateCommand()
        command.CommandText <- sql
        Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture)

    let private scalarString path sql =
        let builder = SqliteConnectionStringBuilder()
        builder.DataSource <- path
        builder.Mode <- SqliteOpenMode.ReadWrite
        builder.ForeignKeys <- true
        builder.Pooling <- false
        use connection = new SqliteConnection(builder.ToString())
        connection.Open()
        use command = connection.CreateCommand()
        command.CommandText <- sql
        Convert.ToString(command.ExecuteScalar(), CultureInfo.InvariantCulture)

    let private seedUser (connection: SqliteConnection) id name role status botToken =
        execute connection
            "INSERT INTO users(id, name, role, status, bot_token, created_at, updated_at) VALUES ($id, $name, $role, $status, $token, '2025-01-01 00:00:00', '2025-01-01 00:00:00')"
            [ "$id", box id
              "$name", box name
              "$role", box role
              "$status", box status
              "$token", (match botToken with Some value -> box value | None -> box DBNull.Value) ]
        |> ignore

    let private seedAuthUser (connection: SqliteConnection) id name emailAddress role status digest =
        execute connection
            "INSERT INTO users(id, name, role, status, email_address, password_digest, created_at, updated_at) VALUES ($id, $name, $role, $status, $email, $digest, '2025-01-01 00:00:00', '2025-01-01 00:00:00')"
            [ "$id", box id
              "$name", box name
              "$email", box emailAddress
              "$role", box role
              "$status", box status
              "$digest", (match digest with Some value -> box value | None -> box DBNull.Value) ]
        |> ignore

    let private seedSession (connection: SqliteConnection) id userId token ipAddress userAgent lastActive created updated =
        execute connection
            "INSERT INTO sessions(id, user_id, token, ip_address, user_agent, last_active_at, created_at, updated_at) VALUES ($id, $user, $token, $ip, $agent, $last, $created, $updated)"
            [ "$id", box id
              "$user", box userId
              "$token", box token
              "$ip", (match ipAddress with Some value -> box value | None -> box DBNull.Value)
              "$agent", (match userAgent with Some value -> box value | None -> box DBNull.Value)
              "$last", box lastActive
              "$created", box created
              "$updated", box updated ]
        |> ignore

    let private seedPushSubscription (connection: SqliteConnection) id userId endpoint =
        execute connection
            "INSERT INTO push_subscriptions(id, user_id, endpoint, created_at, updated_at) VALUES ($id, $user, $endpoint, '2025-01-01 00:00:00', '2025-01-01 00:00:00')"
            [ "$id", box id
              "$user", box userId
              "$endpoint", (match endpoint with Some value -> box value | None -> box DBNull.Value) ]
        |> ignore

    let private seedRoom (connection: SqliteConnection) id creatorId name kind created updated =
        execute connection
            "INSERT INTO rooms(id, created_at, creator_id, name, type, updated_at) VALUES ($id, $created, $creator, $name, $kind, $updated)"
            [ "$id", box id
              "$created", box created
              "$creator", box creatorId
              "$name", (match name with Some value -> box value | None -> box DBNull.Value)
              "$kind", box kind
              "$updated", box updated ]
        |> ignore

    let private seedMembership (connection: SqliteConnection) id userId roomId =
        execute connection
            "INSERT INTO memberships(id, room_id, user_id, created_at, updated_at) VALUES ($id, $room, $user, '2025-01-01 00:00:00', '2025-01-01 00:00:00')"
            [ "$id", box id; "$room", box roomId; "$user", box userId ]
        |> ignore

    let private seedBan (connection: SqliteConnection) id userId ip =
        execute connection
            "INSERT INTO bans(id, user_id, ip_address, created_at, updated_at) VALUES ($id, $user, $ip, '2025-01-01 00:00:00', '2025-01-01 00:00:00')"
            [ "$id", box id; "$user", box userId; "$ip", box ip ]
        |> ignore

    let private openStore path =
        match AuthenticationSqliteProvider.OpenExisting(path) with
        | Ok provider -> provider
        | Error error -> failwith $"open existing test database failed: {error}"

    let private openStoreWith path passwordVerifier tokenGenerator =
        match AuthenticationSqliteProvider.OpenExisting(path, passwordVerifier, tokenGenerator) with
        | Ok provider -> provider
        | Error error -> failwith $"open existing test database with injected dependencies failed: {error}"

    let private expectOk label result =
        match result with
        | Ok value -> value
        | Error error -> failwith $"{label}: expected success, got {error}"

    let private expectError expected label result =
        match result with
        | Error actual -> equal expected actual label
        | Ok _ -> failwith $"{label}: expected {expected}, got success"

    let private assertGroup label test =
        test ()
        groups <- groups + 1
        printfn "PASS %s" label

    let private sessionLookupAndUserRestoration () =
        let path =
            createFixture "session-user-lookups" (fun connection ->
                let mutable id = 1L
                for role in 0L .. 2L do
                    for status in 0L .. 2L do
                        seedUser connection id $"user-{id}" role status None
                        id <- id + 1L
                seedSession connection 1L 1L "session-token" (Some "192.0.2.1") (Some "agent")
                    "2025-01-01T02:03:04.123456+02:00" "2025-01-01 00:00:00" "2025-01-01 00:00:00"
                seedSession connection 2L 2L "deactivated-session" None None
                    "2025-01-01 00:00:00" "2024-12-31 23:00:00" "2024-12-31 23:00:00")
        let provider = openStore path
        let session =
            provider.FindSessionByToken(SessionToken "session-token")
            |> expectOk "find a session by exact token"
            |> Option.defaultWith (fun () -> failwith "known session token was not found")
        equal (SessionId 1L) session.Id "session ID is typed and retained"
        equal (UserId 1L) session.UserId "session user ID is retained"
        equal (SessionToken "session-token") session.Token "session token is returned unchanged"
        equal (Some "192.0.2.1") session.IpAddress "session IP is read"
        equal (Some "agent") session.UserAgent "session user-agent is read"
        equal (DateTimeOffset(2025, 1, 1, 0, 3, 4, TimeSpan.Zero).AddTicks(1_234_560L)) session.LastActiveAt "session timestamp offsets compare as UTC instants"
        equal (DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero)) session.CreatedAt "created timestamp parses in UTC"
        equal None (provider.FindSessionByToken(SessionToken "missing") |> expectOk "missing session token") "missing token is an ordinary absence"
        equal None (provider.FindSessionByToken(SessionToken "x' OR 1=1 --") |> expectOk "injection-shaped token") "token input remains a bound exact value"
        expectError InvalidInput "null token input" (provider.FindSessionByToken(SessionToken null))

        let roles = [| AuthUserRole.Member; AuthUserRole.Administrator; AuthUserRole.Bot |]
        let statuses = [| AuthUserStatus.Active; AuthUserStatus.Deactivated; AuthUserStatus.Banned |]
        for roleIndex in 0 .. 2 do
            for statusIndex in 0 .. 2 do
                let userId = int64 (roleIndex * 3 + statusIndex + 1)
                let user =
                    provider.FindUserById(UserId userId)
                    |> expectOk "find each closed role/status combination"
                    |> Option.defaultWith (fun () -> failwith $"user {userId} was missing")
                equal roles[roleIndex] user.Role $"role code {roleIndex} maps to its closed enum"
                equal statuses[statusIndex] user.Status $"status code {statusIndex} maps to its closed enum"
        let deactivated = provider.FindUserById(UserId 2L) |> expectOk "restore a deactivated user" |> Option.get
        equal AuthUserStatus.Deactivated deactivated.Status "find-by-ID does not silently filter deactivated users"
        let sessionForDeactivated = provider.FindSessionByToken(SessionToken "deactivated-session") |> expectOk "find deactivated user's retained session" |> Option.get
        equal deactivated.Id sessionForDeactivated.UserId "session lookup returns its user ID without status policy"
        equal None (provider.FindUserById(UserId 999L) |> expectOk "missing user ID") "missing user is an ordinary absence"

    let private malformedStoredValuesFailClosed () =
        let path =
            createFixture "malformed-stored-values" (fun connection ->
                seedUser connection 1L "bad-role" 9L 0L None
                seedUser connection 2L "bad-status" 0L 9L None
                seedSession connection 1L 1L "bad-time" None None "yesterday" "2025-01-01 00:00:00" "2025-01-01 00:00:00"
                seedSession connection 2L 2L "bad-created-time" None None "2025-01-01 00:00:00" "2025-02-30 00:00:00" "2025-01-01 00:00:00"
                seedRoom connection 1L 1L (Some "Bad kind") "Rooms::Unknown" "2025-01-01 00:00:00" "2025-01-01 00:00:00"
                seedRoom connection 2L 1L (Some "Bad time") "Rooms::Open" "bad timestamp" "2025-01-01 00:00:00"
                seedMembership connection 1L 1L 1L
                seedMembership connection 2L 1L 2L)
        let provider = openStore path
        expectError Unavailable "unknown role code fails closed" (provider.FindUserById(UserId 1L))
        expectError Unavailable "unknown status code fails closed" (provider.FindUserById(UserId 2L))
        expectError Unavailable "invalid session activity timestamp fails closed" (provider.FindSessionByToken(SessionToken "bad-time"))
        expectError Unavailable "invalid session creation timestamp fails closed" (provider.FindSessionByToken(SessionToken "bad-created-time"))
        expectError Unavailable "unknown room STI kind fails closed" (provider.FindRoomForUser(UserId 1L, RoomId 1L))
        expectError Unavailable "invalid room timestamp fails closed" (provider.FindRoomForUser(UserId 1L, RoomId 2L))

    let private botKeyShapeAndEligibility () =
        let path =
            createFixture "bot-key-shape" (fun connection ->
                seedUser connection 3L "active bot" 2L 0L (Some "token")
                seedUser connection 4L "deactivated bot" 2L 1L (Some "old-token")
                seedUser connection 5L "banned bot" 2L 2L (Some "banned-token")
                seedUser connection 6L "active member" 0L 0L (Some "member-token")
                seedUser connection 7L "active administrator" 1L 0L (Some "admin-token"))
        let provider = openStore path
        let activeBot = provider.AuthenticateBotKey(BotKey "3-token") |> expectOk "active bot key" |> Option.get
        equal (UserId 3L) activeBot.Id "valid bot key returns the active bot"
        equal AuthUserRole.Bot activeBot.Role "active bot lookup retains bot role"
        equal AuthUserStatus.Active activeBot.Status "active bot lookup retains active status"
        equal (Some activeBot) (provider.AuthenticateBotKey(BotKey "3-token-suffix") |> expectOk "Rust first-two-segment suffix behavior") "later suffix segments are ignored by the DB lookup"
        equal (Some activeBot) (provider.AuthenticateBotKey(BotKey "3-token-") |> expectOk "trailing empty suffix after a valid token") "the first two Rust split segments authenticate even when a later segment is empty"
        equal None (provider.AuthenticateBotKey(BotKey "3-") |> expectOk "empty token segment") "an empty token does not match the stored token"
        equal None (provider.AuthenticateBotKey(BotKey "3-wrong") |> expectOk "wrong bot token") "wrong token does not authenticate"
        equal None (provider.AuthenticateBotKey(BotKey "4-old-token") |> expectOk "deactivated bot") "deactivated bot does not authenticate"
        equal None (provider.AuthenticateBotKey(BotKey "5-banned-token") |> expectOk "banned bot") "banned bot does not authenticate"
        equal None (provider.AuthenticateBotKey(BotKey "6-member-token") |> expectOk "member with a token") "non-bot role does not authenticate as bot"
        equal None (provider.AuthenticateBotKey(BotKey "7-admin-token") |> expectOk "administrator with a token") "administrator role does not authenticate as bot"
        equal None (provider.AuthenticateBotKey(BotKey "3") |> expectOk "missing bot-key delimiter") "missing token segment returns no match"
        equal None (provider.AuthenticateBotKey(BotKey "not-an-id-token") |> expectOk "malformed bot-key ID") "malformed ID remains a parameterized non-match"
        equal None (provider.AuthenticateBotKey(BotKey "3' OR 1=1 ---token") |> expectOk "injection-shaped bot key") "bot-key values cannot alter the query"
        equal None (provider.AuthenticateBotKey(BotKey " 3-token ") |> expectOk "raw bot key is not trimmed") "DB operation preserves exact caller input"
        equal (Some activeBot)
            (provider.AuthenticateBotKey(BotKey(" 3-token ".Trim())) |> expectOk "simulate upstream controller trimming")
            "controller-style trimming is an explicit caller step"
        expectError InvalidInput "null bot key" (provider.AuthenticateBotKey(BotKey null))

    let private bansAndMembershipScopedRooms () =
        let path =
            createFixture "bans-room-scope" (fun connection ->
                seedUser connection 1L "member" 0L 0L None
                seedUser connection 2L "administrator" 1L 0L None
                seedBan connection 1L 1L "203.0.113.7"
                seedRoom connection 10L 1L (Some "Open") "Rooms::Open" "2025-01-01 00:00:00" "2025-01-01 00:00:00"
                seedRoom connection 11L 1L (Some "Closed") "Rooms::Closed" "2025-01-01 00:00:00" "2025-01-01 00:00:00"
                seedRoom connection 12L 1L None "Rooms::Direct" "2025-01-01 00:00:00" "2025-01-01 00:00:00"
                seedMembership connection 1L 1L 10L
                seedMembership connection 2L 1L 11L
                seedMembership connection 3L 1L 12L
                seedMembership connection 4L 999L 999L)
        let provider = openStore path
        check (provider.IsIpBanned(RemoteIp "203.0.113.7") |> expectOk "exact banned IP") "exact IP membership is banned"
        check (not (provider.IsIpBanned(RemoteIp "203.0.113.70") |> expectOk "different IP string")) "different IP is not banned"
        check (not (provider.IsIpBanned(RemoteIp "203.0.113.7' OR 1=1 --") |> expectOk "injection-shaped IP")) "IP input remains a bound exact value"
        expectError InvalidInput "null remote IP" (provider.IsIpBanned(RemoteIp null))
        let openRoom = provider.FindRoomForUser(UserId 1L, RoomId 10L) |> expectOk "member room lookup" |> Option.get
        equal (RoomId 10L) openRoom.Id "room lookup returns the requested member room"
        equal RoomKind.Open openRoom.Kind "open STI class maps to its closed room kind"
        let closedRoom = provider.FindRoomForUser(UserId 1L, RoomId 11L) |> expectOk "closed room lookup" |> Option.get
        equal RoomKind.Closed closedRoom.Kind "closed STI class maps to its closed room kind"
        let directRoom = provider.FindRoomForUser(UserId 1L, RoomId 12L) |> expectOk "direct room lookup" |> Option.get
        equal RoomKind.Direct directRoom.Kind "direct STI class maps to its closed room kind"
        equal None (provider.FindRoomForUser(UserId 2L, RoomId 10L) |> expectOk "administrator without membership") "administrator receives no membership bypass"
        equal None (provider.FindRoomForUser(UserId 1L, RoomId 999L) |> expectOk "orphan membership has no room") "orphan membership does not produce a room row"
        equal None (provider.FindRoomForUser(UserId 1L, RoomId 999L) |> expectOk "missing room lookup") "missing room is an ordinary absence"
        equal None (provider.FindRoomForUser(UserId 999L, RoomId 10L) |> expectOk "nonmember user lookup") "room is invisible without an exact membership"

    let private refreshConditionalAndReopen () =
        let path =
            createFixture "session-refresh" (fun connection ->
                seedUser connection 1L "member" 0L 0L None
                seedSession connection 1L 1L "exact-boundary" (Some "old-ip") (Some "old-agent")
                    "2025-01-01 00:00:00.000001" "2024-12-31 00:00:00" "2025-01-01 00:00:00.000001"
                seedSession connection 2L 1L "one-microsecond-old" (Some "old-ip") (Some "old-agent")
                    "2025-01-01 00:00:00.000000" "2024-12-31 00:00:00" "2025-01-01 00:00:00.000000"
                seedSession connection 3L 1L "offset-old" (Some "offset-ip") (Some "offset-agent")
                    "2025-01-01 02:00:00.000000+02:00" "2024-12-31 00:00:00" "2025-01-01 00:00:00.000000+02:00"
                seedSession connection 4L 1L "fresh-session" (Some "keep-ip") (Some "keep-agent")
                    "2025-01-01 00:00:00.000002" "2024-12-31 00:00:00" "2025-01-01 00:00:00.000002"
                seedSession connection 0L 1L "zero-id-session" None None
                    "2024-12-31 23:00:00" "2024-12-31 00:00:00" "2024-12-31 23:00:00"
                seedSession connection -1L 1L "negative-id-session" None None
                    "2024-12-31 23:00:00" "2024-12-31 00:00:00" "2024-12-31 23:00:00")
        let provider = openStore path
        let cutoff = DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero).AddTicks(10L)
        let now = DateTimeOffset(2025, 1, 1, 4, 0, 0, TimeSpan.FromHours(2)).AddTicks(1_234_567L)
        let boundaryBefore = provider.FindSessionByToken(SessionToken "exact-boundary") |> expectOk "capture exact-boundary row" |> Option.get
        let boundaryAfter =
            provider.RefreshSessionBefore
                { SessionId = SessionId 1L
                  Cutoff = cutoff
                  Now = now
                  IpAddress = Some "new-ip"
                  UserAgent = Some "new-agent" }
            |> expectOk "refresh at the exact cutoff"
        equal boundaryBefore boundaryAfter "a session exactly at cutoff is unchanged"
        let subMicroBoundary =
            provider.RefreshSessionBefore
                { SessionId = SessionId 1L
                  Cutoff = cutoff.AddTicks(1L)
                  Now = now
                  IpAddress = Some "submicro-ip"
                  UserAgent = Some "submicro-agent" }
            |> expectOk "compare against the full sub-microsecond cutoff"
        equal (Some "submicro-ip") subMicroBoundary.IpAddress "a 1µs activity is older than a 1.1µs cutoff"
        equal (DateTimeOffset(2025, 1, 1, 2, 0, 0, TimeSpan.Zero).AddTicks(1_234_560L)) subMicroBoundary.LastActiveAt "now writes remain truncated to database microsecond precision"

        let refreshed =
            provider.RefreshSessionBefore
                { SessionId = SessionId 2L
                  Cutoff = cutoff
                  Now = now
                  IpAddress = None
                  UserAgent = None }
            |> expectOk "refresh one microsecond before cutoff"
        equal (SessionId 2L) refreshed.Id "refresh preserves stable session ID"
        equal (UserId 1L) refreshed.UserId "refresh preserves owner ID"
        equal (SessionToken "one-microsecond-old") refreshed.Token "refresh preserves session token"
        equal None refreshed.IpAddress "refresh clears IP when None was supplied"
        equal None refreshed.UserAgent "refresh clears user-agent when None was supplied"
        equal (DateTimeOffset(2025, 1, 1, 2, 0, 0, TimeSpan.Zero).AddTicks(1_234_560L)) refreshed.LastActiveAt "refresh writes supplied now in UTC and truncates to microseconds"
        equal refreshed.LastActiveAt refreshed.UpdatedAt "refresh writes the same explicit time to updated_at"
        equal (DateTimeOffset(2024, 12, 31, 0, 0, 0, TimeSpan.Zero)) refreshed.CreatedAt "refresh preserves created_at"
        let reopened = openStore path
        equal (Some refreshed) (reopened.FindSessionByToken(SessionToken "one-microsecond-old") |> expectOk "reopen and load refreshed session") "session refresh persists after reopening"
        equal 0L (scalarLong path "SELECT COUNT(*) FROM sessions WHERE id = 2 AND ip_address IS NOT NULL AND user_agent IS NOT NULL") "None IP/user-agent are persisted as SQL NULL"

        let offsetRefresh =
            reopened.RefreshSessionBefore
                { SessionId = SessionId 3L
                  Cutoff = cutoff
                  Now = now
                  IpAddress = Some "2001:db8::2"
                  UserAgent = Some "new-agent" }
            |> expectOk "compare offset timestamp by instant"
        equal (Some "2001:db8::2") offsetRefresh.IpAddress "offset-form timestamp older by instant is refreshed"
        equal (DateTimeOffset(2025, 1, 1, 2, 0, 0, TimeSpan.Zero).AddTicks(1_234_560L)) offsetRefresh.LastActiveAt "offset-form refresh uses canonical UTC time"
        let freshBefore = reopened.FindSessionByToken(SessionToken "fresh-session") |> expectOk "capture fresh row" |> Option.get
        let freshAfter =
            reopened.RefreshSessionBefore
                { SessionId = SessionId 4L
                  Cutoff = cutoff
                  Now = now
                  IpAddress = Some "ignored-ip"
                  UserAgent = Some "ignored-agent" }
            |> expectOk "fresh session no-op"
        equal freshBefore freshAfter "a session newer than cutoff is left byte-for-byte equivalent as a row"
        equal "2025-01-01 00:00:00.000002" (scalarString path "SELECT last_active_at FROM sessions WHERE id = 4") "no-op leaves stored timestamp text unchanged"
        let zeroTokenRow = reopened.FindSessionByToken(SessionToken "zero-id-session") |> expectOk "find zero-ID session" |> Option.get
        equal (SessionId 0L) zeroTokenRow.Id "token lookup imports a stored zero session ID"
        let zeroId =
            reopened.RefreshSessionBefore
                { SessionId = SessionId 0L
                  Cutoff = cutoff
                  Now = now
                  IpAddress = Some "zero-id-ip"
                  UserAgent = Some "zero-id-agent" }
            |> expectOk "refresh stored zero session ID"
        equal (SessionId 0L) zeroId.Id "SQLite session ID zero is retained and refreshable"
        let negativeTokenRow = reopened.FindSessionByToken(SessionToken "negative-id-session") |> expectOk "find negative-ID session" |> Option.get
        equal (SessionId(-1L)) negativeTokenRow.Id "token lookup imports a stored negative session ID"
        let negativeId =
            reopened.RefreshSessionBefore
                { SessionId = SessionId(-1L)
                  Cutoff = cutoff
                  Now = now
                  IpAddress = Some "negative-id-ip"
                  UserAgent = Some "negative-id-agent" }
            |> expectOk "refresh stored negative session ID"
        equal (SessionId(-1L)) negativeId.Id "SQLite negative session ID is retained and refreshable"
        let persistedNegative =
            (openStore path).FindSessionByToken(SessionToken "negative-id-session")
            |> expectOk "reopen negative session"
        equal (Some negativeId) persistedNegative "negative session refresh is persisted"
        expectError NotFound "refresh missing session" (reopened.RefreshSessionBefore { SessionId = SessionId 999L; Cutoff = cutoff; Now = now; IpAddress = None; UserAgent = None })
        expectError InvalidInput "refresh cutoff after now" (reopened.RefreshSessionBefore { SessionId = SessionId 1L; Cutoff = now; Now = cutoff; IpAddress = None; UserAgent = None })
        expectError InvalidInput "refresh null IP option" (reopened.RefreshSessionBefore { SessionId = SessionId 1L; Cutoff = cutoff; Now = now; IpAddress = Some null; UserAgent = None })

    let private refreshRollbackAndMalformedTimestamp () =
        let path =
            createFixture "refresh-trigger-rollback" (fun connection ->
                seedUser connection 1L "member" 0L 0L None
                seedSession connection 1L 1L "trigger-session" (Some "old-ip") (Some "old-agent")
                    "2025-01-01 00:00:00" "2024-12-31 00:00:00" "2025-01-01 00:00:00"
                execute connection
                    "CREATE TRIGGER reject_session_refresh BEFORE UPDATE OF last_active_at ON sessions BEGIN SELECT RAISE(ABORT, 'private trigger detail'); END"
                    []
                |> ignore)
        let provider = openStore path
        let before = provider.FindSessionByToken(SessionToken "trigger-session") |> expectOk "read before trigger rollback" |> Option.get
        expectError Unavailable "trigger-aborted refresh maps to closed provider error"
            (provider.RefreshSessionBefore
                { SessionId = SessionId 1L
                  Cutoff = DateTimeOffset(2025, 1, 1, 1, 0, 0, TimeSpan.Zero)
                  Now = DateTimeOffset(2025, 1, 1, 2, 0, 0, TimeSpan.Zero)
                  IpAddress = Some "new-ip"
                  UserAgent = Some "new-agent" })
        equal 0L (scalarLong path "SELECT COUNT(*) FROM sessions WHERE id = 1 AND ip_address = 'new-ip'") "trigger failure leaves the session row unchanged"
        equal before (provider.FindSessionByToken(SessionToken "trigger-session") |> expectOk "read after trigger rollback" |> Option.get) "failed refresh has no partial mutation"
        let malformedPath =
            createFixture "refresh-malformed-time" (fun connection ->
                seedUser connection 1L "member" 0L 0L None
                seedSession connection 1L 1L "malformed-refresh" None None "bad time" "2024-12-31 00:00:00" "2024-12-31 00:00:00")
        let malformedProvider = openStore malformedPath
        expectError Unavailable "refresh cannot compare an invalid stored timestamp"
            (malformedProvider.RefreshSessionBefore
                { SessionId = SessionId 1L
                  Cutoff = DateTimeOffset(2025, 1, 1, 1, 0, 0, TimeSpan.Zero)
                  Now = DateTimeOffset(2025, 1, 1, 2, 0, 0, TimeSpan.Zero)
                  IpAddress = None
                  UserAgent = None })

    let private newerConcurrentRefreshCannotBeOverwritten () =
        let path =
            createFixture "refresh-concurrent" (fun connection ->
                seedUser connection 1L "member" 0L 0L None
                seedSession connection 1L 1L "concurrent-session" None None
                    "2025-01-01 00:00:00" "2024-12-31 00:00:00" "2025-01-01 00:00:00")
        let provider = openStore path
        let olderNow = DateTimeOffset(2025, 1, 1, 2, 0, 0, TimeSpan.Zero)
        let newerNow = DateTimeOffset(2025, 1, 1, 4, 0, 0, TimeSpan.Zero)
        let older =
            Task.Run(Func<Result<SessionRow, DbError>>(fun () ->
                provider.RefreshSessionBefore
                    { SessionId = SessionId 1L
                      Cutoff = olderNow.AddHours(-1.0)
                      Now = olderNow
                      IpAddress = Some "older-ip"
                      UserAgent = Some "older-agent" }))
        let newer =
            Task.Run(Func<Result<SessionRow, DbError>>(fun () ->
                provider.RefreshSessionBefore
                    { SessionId = SessionId 1L
                      Cutoff = newerNow.AddHours(-1.0)
                      Now = newerNow
                      IpAddress = Some "newer-ip"
                      UserAgent = Some "newer-agent" }))
        Task.WaitAll([| older :> Task; newer :> Task |])
        expectOk "older concurrent refresh completes" older.Result |> ignore
        expectOk "newer concurrent refresh completes" newer.Result |> ignore
        let final = provider.FindSessionByToken(SessionToken "concurrent-session") |> expectOk "read after concurrent refreshes" |> Option.get
        equal newerNow final.LastActiveAt "later concurrent request wins regardless of lock order"
        equal (Some "newer-ip") final.IpAddress "an older refresh cannot overwrite newer request metadata"
        equal (Some "newer-agent") final.UserAgent "an older refresh cannot overwrite newer user-agent"

    let private openExistingDoesNotCreateOrExposeDatabase () =
        let missingPath = Path.Combine(evidenceDirectory, $"missing-{Guid.NewGuid():N}.db")
        expectError Unavailable "missing database path" (AuthenticationSqliteProvider.OpenExisting(missingPath))
        check (not (File.Exists(missingPath))) "opening a missing path never creates a SQLite file"
        expectError InvalidInput "blank database path" (AuthenticationSqliteProvider.OpenExisting("  "))
        let path =
            createFixture "closed-sql-api" (fun connection ->
                seedUser connection 1L "member" 0L 0L None
                seedBan connection 1L 1L "192.0.2.9")
        let provider = openStore path
        check (provider.IsIpBanned(RemoteIp "192.0.2.9") |> expectOk "fixed ban lookup") "only the fixed typed operation is exposed"

    let private passwordVectorDocument =
        lazy (
            let path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "rails-password-vectors.json")
            JsonDocument.Parse(File.ReadAllText(path)))

    let private passwordVector name =
        let vectors = passwordVectorDocument.Value.RootElement.GetProperty("vectors")
        vectors.EnumerateArray()
        |> Seq.find (fun vector -> vector.GetProperty("name").GetString() = name)

    let private requiredString (element: JsonElement) (propertyName: string) =
        match element.GetProperty(propertyName).GetString() with
        | null -> failwith $"password vector field {propertyName} was null"
        | value -> value

    let private stringArray (element: JsonElement) (propertyName: string) =
        element.GetProperty(propertyName).EnumerateArray()
        |> Seq.map (fun item -> item.GetString())
        |> Seq.toArray

    let private authenticateWithDefault path email password =
        (openStore path).AuthenticatePassword(email, password)
        |> expectOk "authenticate with the default Rails bcrypt verifier"

    let private railsBcryptGoldenAuthentication () =
        let secret = passwordVector "secret"
        let unicode = passwordVector "unicode"
        let first72 = passwordVector "first-72-bytes"
        let malformedDigest = "not-a-bcrypt-digest"
        let path =
            createFixture "rails-password-goldens" (fun connection ->
                seedAuthUser connection 1L "Secret User" "secret@example.test" 0L 0L (Some(requiredString secret "digest"))
                seedAuthUser connection 2L "Unicode User" "unicode@example.test" 1L 0L (Some(requiredString unicode "digest"))
                seedAuthUser connection 3L "Long Password User" "long@example.test" 2L 0L (Some(requiredString first72 "digest"))
                seedAuthUser connection 4L "Malformed User" "malformed@example.test" 0L 0L (Some malformedDigest))

        for password in stringArray secret "valid" do
            match authenticateWithDefault path "secret@example.test" password with
            | Some user ->
                equal (UserId 1L) user.Id "Rails bcrypt vector authenticates its known secret"
                equal AuthUserRole.Member user.Role "password authentication returns only the auth projection"
            | None -> failwith "a Rails bcrypt known-secret vector did not authenticate"
        for password in stringArray secret "invalid" do
            equal None (authenticateWithDefault path "secret@example.test" password) "wrong password is rejected"

        for password in stringArray unicode "valid" do
            match authenticateWithDefault path "unicode@example.test" password with
            | Some user ->
                equal (UserId 2L) user.Id "Rails UTF-8 bcrypt vector authenticates"
                equal AuthUserRole.Administrator user.Role "an active administrator authenticates with a stored digest"
            | None -> failwith "a Rails UTF-8 bcrypt vector did not authenticate"
        for password in stringArray unicode "invalid" do
            equal None (authenticateWithDefault path "unicode@example.test" password) "a different UTF-8 password is rejected"

        for password in stringArray first72 "valid" do
            match authenticateWithDefault path "long@example.test" password with
            | Some user ->
                equal (UserId 3L) user.Id "Rails bcrypt checks the first 72 password bytes"
                equal AuthUserRole.Bot user.Role "an active bot role authenticates when it has a password digest"
            | None -> failwith "a Rails first-72-byte bcrypt vector did not authenticate"
        for password in stringArray first72 "invalid" do
            equal None (authenticateWithDefault path "long@example.test" password) "a different first-72-byte prefix is rejected"

        equal None (authenticateWithDefault path "malformed@example.test" (requiredString secret "password")) "malformed digest fails closed"
        equal 0L (scalarLong path "SELECT COUNT(*) FROM sessions") "successful and failed credential checks do not create sessions"
        equal 4L (scalarLong path "SELECT COUNT(*) FROM users") "credential checks do not mutate their synthetic users"

    let private passwordLookupPolicyAndDummyVerification () =
        let path =
            createFixture "password-lookup-policy" (fun connection ->
                seedAuthUser connection 1L "Member" "Exact@Example.test" 0L 0L (Some "opaque-test-digest")
                seedAuthUser connection 2L "Administrator" "admin@example.test" 1L 0L (Some "opaque-test-digest")
                seedAuthUser connection 3L "Bot" "bot@example.test" 2L 0L (Some "opaque-test-digest")
                seedAuthUser connection 4L "Inactive" "inactive@example.test" 0L 1L (Some "opaque-test-digest")
                seedAuthUser connection 5L "Banned" "banned@example.test" 1L 2L (Some "opaque-test-digest")
                seedAuthUser connection 6L "No Digest" "no-digest@example.test" 0L 0L None
                seedAuthUser connection 7L "Empty Digest" "empty-digest@example.test" 2L 0L (Some ""))
        let calls = ResizeArray<string * string>()
        let verifier password digest =
            calls.Add((password, digest))
            password = "accepted" && digest = "opaque-test-digest"
        let provider = openStoreWith path verifier (fun () -> "123456789ABCDEFGHJKLMNPQ")
        check (provider.AnyUsers() |> expectOk "nonempty user table") "any-users reports existing accounts"

        let expectUser expectedId expectedName expectedRole email =
            let user =
                provider.AuthenticatePassword(email, "accepted")
                |> expectOk "authenticate an active account"
                |> Option.defaultWith (fun () -> failwith "an active account with a verified password was rejected")
            equal
                { Id = UserId expectedId; Name = expectedName; Role = expectedRole; Status = AuthUserStatus.Active }
                user
                "authentication returns an active user without the stored digest"

        expectUser 1L "Member" AuthUserRole.Member "Exact@Example.test"
        expectUser 2L "Administrator" AuthUserRole.Administrator "admin@example.test"
        expectUser 3L "Bot" AuthUserRole.Bot "bot@example.test"
        equal 3 calls.Count "each active role is verified exactly once"

        let dummyDigestAt index =
            let _, digest = calls[index]
            check (digest.StartsWith("$2a$12$", StringComparison.Ordinal) && digest.Length = 60) "unmatched credentials are checked against a cost-12 bcrypt dummy"
            digest

        equal None (provider.AuthenticatePassword("exact@example.test", "accepted") |> expectOk "case-sensitive email lookup") "email case is significant"
        let firstDummy = dummyDigestAt 3
        equal None (provider.AuthenticatePassword(" Exact@Example.test ", "accepted") |> expectOk "untrimmed email lookup") "email whitespace is not removed"
        equal firstDummy (dummyDigestAt 4) "case mismatch uses the shared dummy digest"
        equal None (provider.AuthenticatePassword("missing@example.test", "accepted") |> expectOk "missing email lookup") "missing email does not authenticate"
        equal firstDummy (dummyDigestAt 5) "missing email uses the same dummy digest"
        equal None (provider.AuthenticatePassword("inactive@example.test", "accepted") |> expectOk "inactive account lookup") "deactivated account cannot authenticate"
        equal firstDummy (dummyDigestAt 6) "inactive account uses the same dummy digest"
        equal None (provider.AuthenticatePassword("banned@example.test", "accepted") |> expectOk "banned account lookup") "banned account cannot authenticate"
        equal firstDummy (dummyDigestAt 7) "banned account uses the same dummy digest"

        let beforeNoDigest = calls.Count
        equal None (provider.AuthenticatePassword("no-digest@example.test", "accepted") |> expectOk "account without password digest") "an account without a digest cannot authenticate"
        equal (beforeNoDigest + 1) calls.Count "a null stored digest still performs dummy verification"
        equal firstDummy (dummyDigestAt beforeNoDigest) "passwordless accounts use the shared dummy digest"
        equal None (provider.AuthenticatePassword("empty-digest@example.test", "accepted") |> expectOk "account with empty password digest") "empty digests cannot authenticate"
        equal firstDummy (dummyDigestAt (beforeNoDigest + 1)) "empty digests also perform dummy verification"
        equal 0L (scalarLong path "SELECT COUNT(*) FROM sessions") "credential checks never persist a session"

    let private emptyPasswordSkipsSqliteAndVerifier () =
        let path =
            createFixture "empty-password" (fun connection ->
                seedAuthUser connection 1L "Member" "member@example.test" 0L 0L (Some "opaque-test-digest"))
        let calls = ResizeArray<string * string>()
        let provider =
            openStoreWith path (fun password digest -> calls.Add((password, digest)); true) (fun () -> "123456789ABCDEFGHJKLMNPQ")
        File.Delete(path)
        equal None (provider.AuthenticatePassword("member@example.test", "") |> expectOk "empty password after database removal") "an empty password is a normal failed sign-in"
        equal 0 calls.Count "empty password bypasses password verification"

    let private passwordVerifierRunsWithoutDatabaseLock () =
        let path = createFixture "password-verifier-lock" (fun connection -> seedAuthUser connection 1L "Original" "lock@example.test" 0L 0L (Some "synthetic-digest"))
        let mutable calls = 0
        let verifier _ _ =
            calls <- calls + 1
            let builder = SqliteConnectionStringBuilder(DataSource = path, Mode = SqliteOpenMode.ReadWrite, Pooling = false, DefaultTimeout = 1)
            use connection = new SqliteConnection(builder.ToString())
            connection.Open()
            execute connection "BEGIN EXCLUSIVE; UPDATE users SET name = 'Updated outside lookup' WHERE id = 1; COMMIT;" [] |> ignore
            true
        let provider = openStoreWith path verifier (fun () -> "123456789ABCDEFGHJKLMNPQR")
        let user = provider.AuthenticatePassword("lock@example.test", "synthetic-password") |> expectOk "verify after releasing lookup connection"
        equal 1 calls "password verification occurs once after the lookup"
        equal (Some "Original") (user |> Option.map _.Name) "the safe user projection was captured before verification"
        equal "Updated outside lookup" (scalarString path "SELECT name FROM users WHERE id = 1") "an exclusive writer can commit during verification"
        let broken = openStoreWith path (fun _ _ -> failwith "synthetic private verifier failure") (fun () -> "123456789ABCDEFGHJKLMNPQR")
        expectError Unavailable "unexpected verifier failures are structured" (broken.AuthenticatePassword("lock@example.test", "synthetic-password"))
        expectError Unavailable "dummy verifier failures are structured" (broken.AuthenticatePassword("absent@example.test", "synthetic-password"))

    let private anyUsersOnEmptyDatabase () =
        let path = createFixture "any-users-empty" ignore
        let provider = openStore path
        check (not (provider.AnyUsers() |> expectOk "empty user table")) "any-users is false for an empty table"

    let private validSessionToken (token: string) =
        let alphabet = "123456789ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz"
        not (isNull token) && token.Length = 24 && (token |> Seq.forall (fun character -> alphabet.IndexOf(character) >= 0))

    let private startSession (provider: AuthenticationSqliteProvider) userId now ipAddress userAgent =
        provider.StartSession
            { UserId = UserId userId
              Now = now
              IpAddress = ipAddress
              UserAgent = userAgent }

    let private sessionCreationPersistsAndReopens () =
        let path = createFixture "session-create-reopen" (fun connection -> seedUser connection 1L "member" 0L 0L None)
        let provider = openStore path
        let inputTime = DateTimeOffset(2025, 1, 2, 3, 4, 5, TimeSpan.FromHours(-4.0)).AddTicks(1_234_567L)
        let expectedTime = DateTimeOffset(2025, 1, 2, 7, 4, 5, TimeSpan.Zero).AddTicks(1_234_560L)
        expectError InvalidInput "null session-start record" (provider.StartSession(Unchecked.defaultof<SessionStart>))
        expectError InvalidInput "null IP option value" (startSession provider 1L inputTime (Some null) None)
        expectError InvalidInput "null user-agent option value" (startSession provider 1L inputTime None (Some null))
        equal 0L (scalarLong path "SELECT COUNT(*) FROM sessions") "invalid metadata is rejected before insertion"
        let created =
            startSession provider 1L inputTime (Some "2001:db8::8") (Some "test user-agent")
            |> expectOk "create session with explicit metadata"
        check (validSessionToken (let (SessionToken value) = created.Token in value)) "default token uses the 24-character base58 contract"
        equal (UserId 1L) created.UserId "new session is linked to the requested user"
        equal (Some "2001:db8::8") created.IpAddress "new session returns the supplied IP"
        equal (Some "test user-agent") created.UserAgent "new session returns the supplied user-agent"
        equal expectedTime created.LastActiveAt "session activity time is UTC-normalized and truncated to microseconds"
        equal expectedTime created.CreatedAt "session creation time uses the same explicit UTC instant"
        equal expectedTime created.UpdatedAt "session update time uses the same explicit UTC instant"

        let tokenString = let (SessionToken value) = created.Token in value
        equal "2025-01-02 07:04:05.123456" (scalarString path "SELECT last_active_at FROM sessions WHERE id = 1") "stored session time keeps six fractional digits"
        equal "2025-01-02 07:04:05.123456" (scalarString path "SELECT created_at FROM sessions WHERE id = 1") "stored created time uses the normalized value"
        equal "2025-01-02 07:04:05.123456" (scalarString path "SELECT updated_at FROM sessions WHERE id = 1") "stored updated time uses the normalized value"

        let withoutMetadata =
            startSession provider 1L inputTime None None
            |> expectOk "create session with nullable metadata"
        let (SessionToken secondToken) = withoutMetadata.Token
        check (tokenString <> secondToken) "separate sessions receive unique default tokens"
        check (validSessionToken secondToken) "second default token uses the base58 contract"
        equal None withoutMetadata.IpAddress "None IP remains null in the returned row"
        equal None withoutMetadata.UserAgent "None user-agent remains null in the returned row"
        equal 1L (scalarLong path "SELECT COUNT(*) FROM sessions WHERE id = 2 AND ip_address IS NULL AND user_agent IS NULL") "None metadata is persisted as SQL NULL"

        let reopened = openStore path
        let loaded =
            reopened.FindSessionByToken(created.Token)
            |> expectOk "reopen and find a created session"
            |> Option.defaultWith (fun () -> failwith "created session did not survive reopening")
        equal created loaded "session owner, token, metadata and timestamps survive reopening"
        equal (Some withoutMetadata) (reopened.FindSessionByToken(withoutMetadata.Token) |> expectOk "reopen nullable-metadata session") "nullable-metadata session survives reopening"

    let private sessionCreationRejectsBadTokensAndRollsBack () =
        let path =
            createFixture "session-create-errors" (fun connection ->
                seedUser connection 1L "member" 0L 0L None
                seedSession connection 1L 1L "123456789ABCDEFGHJKLMNPQ" None None
                    "2025-01-01 00:00:00" "2025-01-01 00:00:00" "2025-01-01 00:00:00"
                execute connection
                    "CREATE TRIGGER reject_session_insert BEFORE INSERT ON sessions WHEN NEW.token = '23456789ABCDEFGHJKLMNPQR' BEGIN SELECT RAISE(ABORT, 'private trigger detail'); END"
                    []
                |> ignore)
        let now = DateTimeOffset(2025, 2, 3, 4, 5, 6, TimeSpan.Zero)
        let invoke generator userId =
            openStoreWith path (fun _ _ -> false) generator
            |> fun provider -> startSession provider userId now None None

        expectError Unavailable "token generator exception maps to closed unavailable result" (invoke (fun () -> failwith "private generator detail") 1L)
        expectError Unavailable "malformed generated token is rejected" (invoke (fun () -> "short") 1L)
        expectError ConstraintConflict "token collision maps to a closed conflict" (invoke (fun () -> "123456789ABCDEFGHJKLMNPQ") 1L)
        expectError NotFound "missing session owner maps to not found" (invoke (fun () -> "3456789ABCDEFGHJKLMNPQRS") 999L)
        expectError Unavailable "aborting trigger maps to closed unavailable result" (invoke (fun () -> "23456789ABCDEFGHJKLMNPQR") 1L)
        equal 1L (scalarLong path "SELECT COUNT(*) FROM sessions") "failed session starts leave no partial rows"
        equal 0L (scalarLong path "SELECT COUNT(*) FROM sessions WHERE token = '23456789ABCDEFGHJKLMNPQR'") "trigger failure rolls back its generated-token insert"
        let reopened = openStore path
        equal None (reopened.FindSessionByToken(SessionToken "23456789ABCDEFGHJKLMNPQR") |> expectOk "read after failed insert") "rolled-back session token is absent after reopening"

    let private sessionDestroyIsTargetedIdempotentAndAtomic () =
        let path =
            createFixture "session-destroy" (fun connection ->
                seedUser connection 1L "first user" 0L 0L None
                seedUser connection 2L "second user" 0L 0L None
                seedSession connection 20L 1L "destroy-target" None None
                    "2025-01-01 00:00:00" "2025-01-01 00:00:00" "2025-01-01 00:00:00"
                seedSession connection 21L 1L "same-user-session" None None
                    "2025-01-01 00:00:00" "2025-01-01 00:00:00" "2025-01-01 00:00:00"
                seedSession connection 22L 2L "other-user-session" None None
                    "2025-01-01 00:00:00" "2025-01-01 00:00:00" "2025-01-01 00:00:00")
        let provider = openStore path
        equal () (provider.DeleteSession(SessionId 20L) |> expectOk "delete one session") "session destruction returns Unit"
        equal () (provider.DeleteSession(SessionId 20L) |> expectOk "repeat session destruction") "destroying a missing session is an idempotent success"
        equal 0L (scalarLong path "SELECT COUNT(*) FROM sessions WHERE id = 20") "only the selected session row is deleted"
        equal 2L (scalarLong path "SELECT COUNT(*) FROM sessions") "other users' and same-user sessions remain"
        let reopened = openStore path
        equal None (reopened.FindSessionByToken(SessionToken "destroy-target") |> expectOk "reopen after session destroy") "deleted session stays revoked after reopening"
        check (reopened.FindSessionByToken(SessionToken "same-user-session") |> expectOk "reopen same-user session" |> Option.isSome) "same user's other session remains after reopening"
        check (reopened.FindSessionByToken(SessionToken "other-user-session") |> expectOk "reopen other-user session" |> Option.isSome) "another user's session remains after reopening"

        let failingPath =
            createFixture "session-destroy-failure" (fun connection ->
                seedUser connection 1L "member" 0L 0L None
                seedSession connection 30L 1L "blocked-destroy" None None
                    "2025-01-01 00:00:00" "2025-01-01 00:00:00" "2025-01-01 00:00:00"
                execute connection
                    "CREATE TRIGGER reject_session_delete BEFORE DELETE ON sessions WHEN OLD.id = 30 BEGIN SELECT RAISE(ABORT, 'private trigger detail'); END"
                    []
                |> ignore)
        expectError Unavailable "session delete provider errors map to a closed result" (openStore failingPath |> fun failing -> failing.DeleteSession(SessionId 30L))
        equal 1L (scalarLong failingPath "SELECT COUNT(*) FROM sessions WHERE id = 30") "failed session deletion preserves its row atomically"

    let private pushSubscriptionDestroyIsUserScopedAndAtomic () =
        let endpoint = "https://push.example.test/subscriptions/1"
        let otherEndpoint = "https://push.example.test/subscriptions/2"
        let path =
            createFixture "push-subscription-destroy" (fun connection ->
                seedUser connection 1L "first user" 0L 0L None
                seedUser connection 2L "second user" 0L 0L None
                seedPushSubscription connection 1L 1L (Some endpoint)
                seedPushSubscription connection 2L 1L (Some endpoint)
                seedPushSubscription connection 3L 2L (Some endpoint)
                seedPushSubscription connection 4L 1L (Some otherEndpoint))
        let provider = openStore path
        equal () (provider.DestroyPushSubscriptions(UserId 1L, endpoint) |> expectOk "delete one user's endpoint") "push destruction returns Unit"
        equal 0L (scalarLong path "SELECT COUNT(*) FROM push_subscriptions WHERE user_id = 1 AND endpoint = 'https://push.example.test/subscriptions/1'") "all duplicate matches for the supplied user and endpoint are removed"
        equal 1L (scalarLong path "SELECT COUNT(*) FROM push_subscriptions WHERE user_id = 2 AND endpoint = 'https://push.example.test/subscriptions/1'") "matching endpoint owned by another user remains"
        equal 1L (scalarLong path "SELECT COUNT(*) FROM push_subscriptions WHERE user_id = 1 AND endpoint = 'https://push.example.test/subscriptions/2'") "other endpoints owned by the supplied user remain"
        equal () (provider.DestroyPushSubscriptions(UserId 1L, endpoint) |> expectOk "repeat push endpoint destruction") "missing push matches are an idempotent success"
        equal () (provider.DestroyPushSubscriptions(UserId 1L, "https://push.example.test/missing") |> expectOk "destroy a missing endpoint") "an absent endpoint is an ordinary success"
        expectError InvalidInput "null push endpoint is rejected" (provider.DestroyPushSubscriptions(UserId 1L, null))
        let reopened = openStore path
        equal 0L (scalarLong path "SELECT COUNT(*) FROM push_subscriptions WHERE user_id = 1 AND endpoint = 'https://push.example.test/subscriptions/1'") "scoped endpoint deletion survives reopening"
        equal 1L (scalarLong path "SELECT COUNT(*) FROM push_subscriptions WHERE user_id = 2 AND endpoint = 'https://push.example.test/subscriptions/1'") "other user's endpoint survives reopening"

        let failingPath =
            createFixture "push-subscription-destroy-failure" (fun connection ->
                seedUser connection 1L "member" 0L 0L None
                seedPushSubscription connection 10L 1L (Some "https://push.example.test/blocked")
                seedPushSubscription connection 11L 1L (Some "https://push.example.test/blocked")
                execute connection
                    "CREATE TRIGGER reject_push_subscription_delete BEFORE DELETE ON push_subscriptions WHEN OLD.endpoint = 'https://push.example.test/blocked' BEGIN SELECT RAISE(ABORT, 'private trigger detail'); END"
                    []
                |> ignore)
        expectError Unavailable "push delete provider errors map to a closed result"
            (openStore failingPath |> fun failing -> failing.DestroyPushSubscriptions(UserId 1L, "https://push.example.test/blocked"))
        equal 2L (scalarLong failingPath "SELECT COUNT(*) FROM push_subscriptions WHERE endpoint = 'https://push.example.test/blocked'") "failed multi-row push deletion preserves all matches atomically"

    [<EntryPoint>]
    let main _ =
        try
            assertGroup "session lookup and unfiltered user restoration" sessionLookupAndUserRestoration
            assertGroup "malformed stored values fail closed" malformedStoredValuesFailClosed
            assertGroup "bot-key shape and eligibility" botKeyShapeAndEligibility
            assertGroup "bans and membership-scoped rooms" bansAndMembershipScopedRooms
            assertGroup "conditional session refresh and reopen" refreshConditionalAndReopen
            assertGroup "refresh rollback and malformed timestamps" refreshRollbackAndMalformedTimestamp
            assertGroup "concurrent refresh ordering" newerConcurrentRefreshCannotBeOverwritten
            assertGroup "open-existing path boundary" openExistingDoesNotCreateOrExposeDatabase
            assertGroup "Rails bcrypt golden authentication" railsBcryptGoldenAuthentication
            assertGroup "password lookup policy and dummy verification" passwordLookupPolicyAndDummyVerification
            assertGroup "empty password skips SQLite and verification" emptyPasswordSkipsSqliteAndVerifier
            assertGroup "password verifier releases lookup locks" passwordVerifierRunsWithoutDatabaseLock
            assertGroup "any-users empty database" anyUsersOnEmptyDatabase
            assertGroup "session creation persistence and reopen" sessionCreationPersistsAndReopens
            assertGroup "session token failures and rollback" sessionCreationRejectsBadTokensAndRollsBack
            assertGroup "targeted session destruction and atomic failure" sessionDestroyIsTargetedIdempotentAndAtomic
            assertGroup "user-scoped push-subscription destruction and atomic failure" pushSubscriptionDestroyIsUserScopedAndAtomic
            printfn "PASS %d groups, %d assertions" groups assertions
            0
        with error ->
            eprintfn "FAIL after %d groups and %d assertions: %s" groups assertions error.Message
            eprintfn "%s" error.StackTrace
            1
