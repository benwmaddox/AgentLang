namespace AgentLang.Campfire.Authentication.Persistence.Tests

open System
open System.Globalization
open System.IO
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
            "CREATE TABLE users (id INTEGER PRIMARY KEY, name TEXT NOT NULL, role INTEGER NOT NULL, status INTEGER NOT NULL, bot_token TEXT, created_at TEXT NOT NULL, updated_at TEXT NOT NULL); " +
            "CREATE UNIQUE INDEX users_bot_token_unique ON users(bot_token); " +
            "CREATE TABLE sessions (id INTEGER PRIMARY KEY, user_id INTEGER NOT NULL REFERENCES users(id), token TEXT NOT NULL UNIQUE, ip_address TEXT, user_agent TEXT, last_active_at TEXT NOT NULL, created_at TEXT NOT NULL, updated_at TEXT NOT NULL); " +
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
            printfn "PASS %d groups, %d assertions" groups assertions
            0
        with error ->
            eprintfn "FAIL after %d groups and %d assertions: %s" groups assertions error.Message
            eprintfn "%s" error.StackTrace
            1
