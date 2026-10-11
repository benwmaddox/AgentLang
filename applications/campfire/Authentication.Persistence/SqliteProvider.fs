namespace AgentLang.Campfire.Authentication.Persistence

open System
open System.Globalization
open System.IO
open Microsoft.Data.Sqlite
open AgentLang.Campfire.Persistence

[<Struct>]
type SessionId = SessionId of int64

[<Struct>]
type SessionToken = SessionToken of string

[<Struct>]
type BotKey = BotKey of string

[<Struct>]
type RemoteIp = RemoteIp of string

[<RequireQualifiedAccess>]
type AuthUserRole =
    | Member = 0
    | Administrator = 1
    | Bot = 2

[<RequireQualifiedAccess>]
type AuthUserStatus =
    | Active = 0
    | Deactivated = 1
    | Banned = 2

type SessionRow =
    { Id: SessionId
      UserId: UserId
      Token: SessionToken
      IpAddress: string option
      UserAgent: string option
      LastActiveAt: DateTimeOffset
      CreatedAt: DateTimeOffset
      UpdatedAt: DateTimeOffset }

type SessionRefresh =
    { SessionId: SessionId
      Cutoff: DateTimeOffset
      Now: DateTimeOffset
      IpAddress: string option
      UserAgent: string option }

type AuthUser =
    { Id: UserId
      Name: string
      Role: AuthUserRole
      Status: AuthUserStatus }

exception private ProviderFailure of DbError

module private SqliteDetails =
    let mapError (error: SqliteException) =
        match error.SqliteExtendedErrorCode with
        | 1811 -> Unavailable
        | 1555
        | 2067 -> ConstraintConflict
        | 787 -> NotFound
        | _ when error.SqliteErrorCode = 19 -> InvalidInput
        | _ -> Unavailable

    let private isAsciiDigit value = value >= '0' && value <= '9'

    let private parseOffset (suffix: string) =
        match suffix with
        | ""
        | "Z"
        | " UTC" -> Some TimeSpan.Zero
        | _ when suffix.Length = 6 && (suffix[0] = '+' || suffix[0] = '-') && suffix[3] = ':' ->
            let hours = suffix.Substring(1, 2)
            let minutes = suffix.Substring(4, 2)
            let isDigits text = text |> Seq.forall isAsciiDigit
            if not (isDigits hours) || not (isDigits minutes) then
                None
            else
                let hoursValue = Int32.Parse(hours, CultureInfo.InvariantCulture)
                let minutesValue = Int32.Parse(minutes, CultureInfo.InvariantCulture)
                if hoursValue > 14 || minutesValue > 59 || (hoursValue = 14 && minutesValue <> 0) then
                    None
                else
                    let magnitude = TimeSpan(hoursValue, minutesValue, 0)
                    Some(if suffix[0] = '-' then -magnitude else magnitude)
        | _ -> None

    /// Parse Rails/Rust SQLite UTC text and ordinary explicit-offset forms to an instant.
    /// Fractional input is truncated to the six digits retained by Rails datetime(6).
    let tryParseTimestamp (value: string) =
        if isNull value then
            None
        else
            let text = value.Trim()
            if text.Length < 19 then
                None
            else
                let whole = text.Substring(0, 19)
                let formats = [| "yyyy-MM-dd HH:mm:ss"; "yyyy-MM-dd'T'HH:mm:ss" |]
                let mutable parsed = DateTime.MinValue
                let validSeparators =
                    whole[4] = '-'
                    && whole[7] = '-'
                    && (whole[10] = ' ' || whole[10] = 'T')
                    && whole[13] = ':'
                    && whole[16] = ':'
                if not validSeparators
                   || not (DateTime.TryParseExact(whole, formats, CultureInfo.InvariantCulture, DateTimeStyles.None, &parsed)) then
                    None
                else
                    let suffixStart =
                        let mutable index = 19
                        let mutable microseconds = 0
                        let mutable validFraction = true
                        if index < text.Length && text[index] = '.' then
                            index <- index + 1
                            let firstDigit = index
                            while index < text.Length && isAsciiDigit text[index] do
                                index <- index + 1
                            let digitCount = index - firstDigit
                            if digitCount = 0 then
                                validFraction <- false
                            else
                                let digits = text.Substring(firstDigit, min 6 digitCount).PadRight(6, '0')
                                microseconds <- Int32.Parse(digits, CultureInfo.InvariantCulture)
                        if validFraction then Some(index, microseconds) else None
                    match suffixStart with
                    | None -> None
                    | Some(index, microseconds) ->
                        match parseOffset (text.Substring index) with
                        | None -> None
                        | Some offset ->
                            try
                                let local = DateTime(parsed.Ticks + int64 microseconds * 10L, DateTimeKind.Unspecified)
                                let instant = DateTimeOffset(local, offset).ToUniversalTime()
                                let ticks = instant.Ticks - instant.Ticks % 10L
                                Some(DateTimeOffset(ticks, TimeSpan.Zero))
                            with _ -> None

    let parseDatabaseTimestamp (value: string) =
        match tryParseTimestamp value with
        | Some instant -> instant
        | None -> raise (ProviderFailure Unavailable)

    let formatTimestamp (instant: DateTimeOffset) =
        let utc = instant.ToUniversalTime()
        let ticks = utc.Ticks - utc.Ticks % 10L
        let normalized = DateTimeOffset(ticks, TimeSpan.Zero)
        let whole = normalized.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)
        let microseconds = (normalized.Ticks % TimeSpan.TicksPerSecond) / 10L
        if microseconds = 0L then whole
        else whole + "." + microseconds.ToString("D6", CultureInfo.InvariantCulture)

    let openConnectionString path =
        let builder = SqliteConnectionStringBuilder()
        builder.DataSource <- path
        builder.Mode <- SqliteOpenMode.ReadWrite
        builder.ForeignKeys <- true
        builder.Pooling <- false
        builder.DefaultTimeout <- 5
        builder.ToString()

    let addParameter (command: SqliteCommand) name (value: obj) =
        command.Parameters.AddWithValue(name, if isNull value then box DBNull.Value else value) |> ignore

    let addNullableString (command: SqliteCommand) name value =
        addParameter command name (match value with Some text -> box text | None -> box DBNull.Value)

    let nullableString (reader: SqliteDataReader) index =
        if reader.IsDBNull(index) then None else Some(reader.GetString(index))

    let parseRoomKind value =
        match value with
        | "Rooms::Open" -> RoomKind.Open
        | "Rooms::Closed" -> RoomKind.Closed
        | "Rooms::Direct" -> RoomKind.Direct
        | _ -> raise (ProviderFailure Unavailable)

    let parseRole value =
        match value with
        | 0L -> AuthUserRole.Member
        | 1L -> AuthUserRole.Administrator
        | 2L -> AuthUserRole.Bot
        | _ -> raise (ProviderFailure Unavailable)

    let parseStatus value =
        match value with
        | 0L -> AuthUserStatus.Active
        | 1L -> AuthUserStatus.Deactivated
        | 2L -> AuthUserStatus.Banned
        | _ -> raise (ProviderFailure Unavailable)

type AuthenticationSqliteProvider private (path: string) =
    let connectionString = SqliteDetails.openConnectionString path

    let run operation =
        try
            use connection = new SqliteConnection(connectionString)
            connection.Open()
            Ok(operation connection)
        with
        | ProviderFailure error -> Error error
        | :? SqliteException as error -> Error(SqliteDetails.mapError error)
        | _ -> Error Unavailable

    let readSession (reader: SqliteDataReader) =
        let rawLastActiveAt = reader.GetString(5)
        let row =
            { Id = SessionId(reader.GetInt64(0))
              UserId = UserId(reader.GetInt64(1))
              Token = SessionToken(reader.GetString(2))
              IpAddress = SqliteDetails.nullableString reader 3
              UserAgent = SqliteDetails.nullableString reader 4
              LastActiveAt = SqliteDetails.parseDatabaseTimestamp rawLastActiveAt
              CreatedAt = reader.GetString(6) |> SqliteDetails.parseDatabaseTimestamp
              UpdatedAt = reader.GetString(7) |> SqliteDetails.parseDatabaseTimestamp }
        row, rawLastActiveAt

    let readSessionById (connection: SqliteConnection) (transaction: SqliteTransaction) (sessionId: int64) =
        use command = connection.CreateCommand()
        command.Transaction <- transaction
        command.CommandText <-
            "SELECT id, user_id, token, ip_address, user_agent, last_active_at, created_at, updated_at " +
            "FROM sessions WHERE id = $sessionId LIMIT 1"
        SqliteDetails.addParameter command "$sessionId" (box sessionId)
        command.Prepare()
        use reader = command.ExecuteReader()
        if reader.Read() then Some(readSession reader) else None

    let readUser (reader: SqliteDataReader) =
        { Id = UserId(reader.GetInt64(0))
          Name = reader.GetString(1)
          Role = SqliteDetails.parseRole (reader.GetInt64(2))
          Status = SqliteDetails.parseStatus (reader.GetInt64(3)) }

    static member OpenExisting(path: string) : Result<AuthenticationSqliteProvider, DbError> =
        if String.IsNullOrWhiteSpace path then
            Error InvalidInput
        else
            try
                let fullPath = Path.GetFullPath(path)
                if File.Exists(fullPath) then Ok(AuthenticationSqliteProvider(fullPath))
                else Error Unavailable
            with _ -> Error Unavailable

    member _.FindSessionByToken(SessionToken token) =
        if isNull token then
            Error InvalidInput
        else
            run (fun connection ->
                use command = connection.CreateCommand()
                command.CommandText <-
                    "SELECT id, user_id, token, ip_address, user_agent, last_active_at, created_at, updated_at " +
                    "FROM sessions WHERE token = $token LIMIT 1"
                SqliteDetails.addParameter command "$token" (box token)
                command.Prepare()
                use reader = command.ExecuteReader()
                if reader.Read() then Some(fst (readSession reader)) else None)

    member _.RefreshSessionBefore(refresh: SessionRefresh) =
        if obj.ReferenceEquals(refresh, null) then
            Error InvalidInput
        else
            let (SessionId sessionId) = refresh.SessionId
            let hasNullOption = Option.exists isNull refresh.IpAddress || Option.exists isNull refresh.UserAgent
            let cutoff = refresh.Cutoff.ToUniversalTime()
            let now = refresh.Now.ToUniversalTime()
            if hasNullOption || cutoff > now then
                Error InvalidInput
            else
                run (fun connection ->
                    use transaction = connection.BeginTransaction()
                    let nowUtc = DateTimeOffset(now.Ticks - now.Ticks % 10L, TimeSpan.Zero)
                    let rec updateIfDue retries =
                        match readSessionById connection transaction sessionId with
                        | None -> raise (ProviderFailure NotFound)
                        | Some(current, _) when current.LastActiveAt >= cutoff -> current
                        | Some(_, observedLastActiveText) ->
                            use command = connection.CreateCommand()
                            command.Transaction <- transaction
                            command.CommandText <-
                                "UPDATE sessions SET ip_address = $ipAddress, user_agent = $userAgent, " +
                                "last_active_at = $now, updated_at = $now " +
                                "WHERE id = $sessionId AND last_active_at = $observedLastActiveAt"
                            SqliteDetails.addNullableString command "$ipAddress" refresh.IpAddress
                            SqliteDetails.addNullableString command "$userAgent" refresh.UserAgent
                            SqliteDetails.addParameter command "$now" (box (SqliteDetails.formatTimestamp nowUtc))
                            SqliteDetails.addParameter command "$sessionId" (box sessionId)
                            SqliteDetails.addParameter command "$observedLastActiveAt" (box observedLastActiveText)
                            command.Prepare()
                            let changed = command.ExecuteNonQuery()
                            if changed = 1 then
                                match readSessionById connection transaction sessionId with
                                | Some(updated, _) -> updated
                                | None -> raise (ProviderFailure NotFound)
                            elif retries > 0 then
                                updateIfDue (retries - 1)
                            else
                                raise (ProviderFailure Unavailable)
                    let updated = updateIfDue 2
                    transaction.Commit()
                    updated)

    member _.FindUserById(UserId userId) =
        run (fun connection ->
            use command = connection.CreateCommand()
            command.CommandText <- "SELECT id, name, role, status FROM users WHERE id = $userId LIMIT 1"
            SqliteDetails.addParameter command "$userId" (box userId)
            command.Prepare()
            use reader = command.ExecuteReader()
            if reader.Read() then Some(readUser reader) else None)

    member _.AuthenticateBotKey(BotKey botKey) =
        if isNull botKey then
            Error InvalidInput
        else
            let parts = botKey.Split([| '-' |], StringSplitOptions.None)
            if parts.Length < 2 then
                Ok None
            else
                run (fun connection ->
                    use command = connection.CreateCommand()
                    command.CommandText <-
                        "SELECT id, name, role, status FROM users " +
                        "WHERE status = 0 AND role = 2 AND id = $id AND bot_token = $token LIMIT 1"
                    SqliteDetails.addParameter command "$id" (box parts[0])
                    SqliteDetails.addParameter command "$token" (box parts[1])
                    command.Prepare()
                    use reader = command.ExecuteReader()
                    if reader.Read() then Some(readUser reader) else None)

    member _.IsIpBanned(RemoteIp remoteIp) =
        if isNull remoteIp then
            Error InvalidInput
        else
            run (fun connection ->
                use command = connection.CreateCommand()
                command.CommandText <- "SELECT EXISTS (SELECT 1 FROM bans WHERE ip_address = $ip LIMIT 1)"
                SqliteDetails.addParameter command "$ip" (box remoteIp)
                command.Prepare()
                Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture) <> 0L)

    member _.FindRoomForUser(UserId userId, RoomId roomId) =
        run (fun connection ->
            use command = connection.CreateCommand()
            command.CommandText <-
                "SELECT rooms.id, rooms.name, rooms.type, rooms.creator_id, rooms.created_at, rooms.updated_at " +
                "FROM rooms INNER JOIN memberships ON rooms.id = memberships.room_id " +
                "WHERE memberships.user_id = $userId AND rooms.id = $roomId LIMIT 1"
            SqliteDetails.addParameter command "$userId" (box userId)
            SqliteDetails.addParameter command "$roomId" (box roomId)
            command.Prepare()
            use reader = command.ExecuteReader()
            if not (reader.Read()) then
                None
            else
                let createdAt = reader.GetString(4)
                let updatedAt = reader.GetString(5)
                SqliteDetails.parseDatabaseTimestamp createdAt |> ignore
                SqliteDetails.parseDatabaseTimestamp updatedAt |> ignore
                Some
                    { Id = RoomId(reader.GetInt64(0))
                      Name = SqliteDetails.nullableString reader 1
                      Kind = SqliteDetails.parseRoomKind (reader.GetString(2))
                      CreatorId = UserId(reader.GetInt64(3))
                      CreatedAt = createdAt
                      UpdatedAt = updatedAt })
