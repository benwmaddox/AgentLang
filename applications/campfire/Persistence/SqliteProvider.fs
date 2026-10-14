namespace AgentLang.Campfire.Persistence

open System
open System.Globalization
open System.IO
open Microsoft.Data.Sqlite

[<Struct>]
type UserId = UserId of int64

[<Struct>]
type RoomId = RoomId of int64

[<Struct>]
type MessageId = MessageId of int64

type RoomKind =
    | Open
    | Closed
    | Direct

type DbError =
    | Unavailable
    | NotFound
    | ConstraintConflict
    | InvalidInput

type RoomRow =
    { Id: RoomId
      Name: string option
      Kind: RoomKind
      CreatorId: UserId
      CreatedAt: string
      UpdatedAt: string }

type MembershipKey =
    { UserId: UserId
      RoomId: RoomId }

type MessageRow =
    { Id: MessageId
      RoomId: RoomId
      CreatorId: UserId
      ClientMessageId: string
      Body: string option
      CreatedAt: string
      UpdatedAt: string }

type MessageWithCreator =
    { Id: MessageId
      RoomId: RoomId
      CreatorId: UserId
      CreatedAtMs: int64
      UpdatedAtMs: int64
      ClientMessageId: string
      CreatedAt: string
      CreatorName: string
      CreatorBio: string
      CreatorUpdatedAt: string
      Body: string option }

type NewMessage =
    { RoomId: RoomId
      CreatorId: UserId
      ClientMessageId: string option
      Body: string option }

type IClock =
    abstract UtcNow: unit -> DateTimeOffset

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

    let formatTimestamp (instant: DateTimeOffset) =
        let utc = instant.ToUniversalTime()
        let whole = utc.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)
        let microseconds = (utc.Ticks % TimeSpan.TicksPerSecond) / 10L
        if microseconds = 0L then whole
        else whole + "." + microseconds.ToString("D6", CultureInfo.InvariantCulture)

    let parseRoomKind value =
        match value with
        | "Rooms::Open" -> Open
        | "Rooms::Closed" -> Closed
        | "Rooms::Direct" -> Direct
        | _ -> raise (ProviderFailure Unavailable)

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

    let nullableString (reader: SqliteDataReader) index =
        if reader.IsDBNull(index) then None else Some(reader.GetString(index))

    let existing (connection: SqliteConnection) sql parameterName (value: int64) =
        use command = connection.CreateCommand()
        command.CommandText <- sql
        addParameter command parameterName (box value)
        command.Prepare()
        Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture) <> 0L

type SqliteProvider private (path: string, clock: IClock, guidSource: unit -> Guid) =
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

    static member OpenExisting(path: string, clock: IClock) =
        SqliteProvider.OpenExisting(path, clock, fun () -> Guid.NewGuid())

    static member OpenExisting(path: string, clock: IClock, guidSource: unit -> Guid) =
        if String.IsNullOrWhiteSpace(path)
           || obj.ReferenceEquals(clock, null)
           || obj.ReferenceEquals(box guidSource, null) then
            Error InvalidInput
        else
            try
                let fullPath = Path.GetFullPath(path)
                if not (File.Exists(fullPath)) then Error Unavailable
                else Ok(SqliteProvider(fullPath, clock, guidSource))
            with
            | _ -> Error Unavailable

    member _.PageRoomsForUser(userId: UserId, afterRoomId: RoomId option, pageSize: int64) =
        if pageSize < 1L || pageSize > 100L then Error InvalidInput
        else
            run (fun connection ->
                let (UserId rawUserId) = userId
                let hasUser =
                    SqliteDetails.existing connection
                        "SELECT EXISTS (SELECT 1 FROM \"users\" WHERE \"id\" = $userId)"
                        "$userId"
                        rawUserId
                if not hasUser then raise (ProviderFailure NotFound)

                use command = connection.CreateCommand()
                command.CommandText <-
                    "SELECT rooms.id, rooms.name, rooms.type, rooms.creator_id, rooms.created_at, rooms.updated_at " +
                    "FROM rooms INNER JOIN memberships ON rooms.id = memberships.room_id " +
                    "WHERE memberships.user_id = $userId AND ($afterRoomId IS NULL OR rooms.id > $afterRoomId) " +
                    "ORDER BY rooms.id LIMIT $pageSize"
                SqliteDetails.addParameter command "$userId" (box rawUserId)
                SqliteDetails.addParameter command "$afterRoomId"
                    (match afterRoomId with Some(RoomId id) -> box id | None -> box DBNull.Value)
                SqliteDetails.addParameter command "$pageSize" (box pageSize)
                command.Prepare()
                use reader = command.ExecuteReader()
                let rows = ResizeArray<RoomRow>()
                while reader.Read() do
                    rows.Add
                        { Id = RoomId(reader.GetInt64(0))
                          Name = SqliteDetails.nullableString reader 1
                          Kind = SqliteDetails.parseRoomKind (reader.GetString(2))
                          CreatorId = UserId(reader.GetInt64(3))
                          CreatedAt = reader.GetString(4)
                          UpdatedAt = reader.GetString(5) }
                List.ofSeq rows)

    member _.MembershipExists(key: MembershipKey) =
        run (fun connection ->
            let (UserId userId) = key.UserId
            let (RoomId roomId) = key.RoomId
            use command = connection.CreateCommand()
            command.CommandText <-
                "SELECT EXISTS (SELECT 1 FROM \"memberships\" WHERE \"user_id\" = $userId AND \"room_id\" = $roomId)"
            SqliteDetails.addParameter command "$userId" (box userId)
            SqliteDetails.addParameter command "$roomId" (box roomId)
            command.Prepare()
            Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture) <> 0L)

    member _.LastPage(roomId: RoomId) =
        run (fun connection ->
            let (RoomId rawRoomId) = roomId
            let hasRoom =
                SqliteDetails.existing connection
                    "SELECT EXISTS (SELECT 1 FROM \"rooms\" WHERE \"id\" = $roomId)"
                    "$roomId"
                    rawRoomId
            if not hasRoom then raise (ProviderFailure NotFound)

            use command = connection.CreateCommand()
            command.CommandText <-
                "SELECT messages.id, messages.room_id, messages.creator_id, messages.client_message_id, " +
                "rich_text.body, messages.created_at, messages.updated_at " +
                "FROM messages LEFT JOIN action_text_rich_texts AS rich_text " +
                "ON rich_text.record_id = messages.id AND rich_text.record_type = 'Message' AND rich_text.name = 'body' " +
                "WHERE messages.room_id = $roomId ORDER BY messages.created_at DESC LIMIT 40"
            SqliteDetails.addParameter command "$roomId" (box rawRoomId)
            command.Prepare()
            use reader = command.ExecuteReader()
            let rows = ResizeArray<MessageRow>()
            while reader.Read() do
                rows.Add
                    { Id = MessageId(reader.GetInt64(0))
                      RoomId = RoomId(reader.GetInt64(1))
                      CreatorId = UserId(reader.GetInt64(2))
                      ClientMessageId = reader.GetString(3)
                      Body = SqliteDetails.nullableString reader 4
                      CreatedAt = reader.GetString(5)
                      UpdatedAt = reader.GetString(6) }
            rows |> Seq.toList |> List.rev)

    member _.LastPageWithCreators(roomId: RoomId) =
        run (fun connection ->
            let (RoomId rawRoomId) = roomId
            let hasRoom =
                SqliteDetails.existing connection
                    "SELECT EXISTS (SELECT 1 FROM \"rooms\" WHERE \"id\" = $roomId)"
                    "$roomId"
                    rawRoomId
            if not hasRoom then raise (ProviderFailure NotFound)

            use command = connection.CreateCommand()
            command.CommandText <-
                "SELECT messages.id, messages.room_id, messages.creator_id, " +
                "CAST(strftime('%s', messages.created_at) AS INTEGER) * 1000 + " +
                "CAST(substr(strftime('%f', messages.created_at), 4) AS INTEGER), " +
                "CAST(strftime('%s', messages.updated_at) AS INTEGER) * 1000 + " +
                "CAST(substr(strftime('%f', messages.updated_at), 4) AS INTEGER), " +
                "messages.client_message_id, rich_text.body, messages.created_at, " +
                "users.name, COALESCE(users.bio, ''), users.updated_at " +
                "FROM messages " +
                "INNER JOIN users ON users.id = messages.creator_id " +
                "LEFT JOIN action_text_rich_texts AS rich_text " +
                "ON rich_text.record_id = messages.id AND rich_text.record_type = 'Message' AND rich_text.name = 'body' " +
                "WHERE messages.room_id = $roomId ORDER BY messages.created_at DESC LIMIT 40"
            SqliteDetails.addParameter command "$roomId" (box rawRoomId)
            command.Prepare()
            use reader = command.ExecuteReader()
            let rows = ResizeArray<MessageWithCreator>()
            while reader.Read() do
                rows.Add
                    { Id = MessageId(reader.GetInt64(0))
                      RoomId = RoomId(reader.GetInt64(1))
                      CreatorId = UserId(reader.GetInt64(2))
                      CreatedAtMs = reader.GetInt64(3)
                      UpdatedAtMs = reader.GetInt64(4)
                      ClientMessageId = reader.GetString(5)
                      Body = SqliteDetails.nullableString reader 6
                      CreatedAt = reader.GetString(7)
                      CreatorName = reader.GetString(8)
                      CreatorBio = reader.GetString(9)
                      CreatorUpdatedAt = reader.GetString(10) }
            rows |> Seq.toList |> List.rev)

    member _.CreateMessage(message: NewMessage) =
        if obj.ReferenceEquals(box message, null) ||
           (message.ClientMessageId |> Option.exists isNull) ||
           (message.Body |> Option.exists isNull) then
            Error InvalidInput
        else
            run (fun connection ->
                use transaction = connection.BeginTransaction()
                let now = SqliteDetails.formatTimestamp (clock.UtcNow())
                let clientMessageId =
                    match message.ClientMessageId with
                    | Some supplied -> supplied
                    | None -> guidSource().ToString("D", CultureInfo.InvariantCulture).ToLowerInvariant()
                let (RoomId roomId) = message.RoomId
                let (UserId creatorId) = message.CreatorId

                use insert = connection.CreateCommand()
                insert.Transaction <- transaction
                insert.CommandText <-
                    "INSERT INTO messages (client_message_id, created_at, creator_id, room_id, updated_at) " +
                    "VALUES ($clientMessageId, $now, $creatorId, $roomId, $now) RETURNING id"
                SqliteDetails.addParameter insert "$clientMessageId" (box clientMessageId)
                SqliteDetails.addParameter insert "$now" (box now)
                SqliteDetails.addParameter insert "$creatorId" (box creatorId)
                SqliteDetails.addParameter insert "$roomId" (box roomId)
                insert.Prepare()
                let messageId = Convert.ToInt64(insert.ExecuteScalar(), CultureInfo.InvariantCulture)

                match message.Body with
                | Some body ->
                    use bodyInsert = connection.CreateCommand()
                    bodyInsert.Transaction <- transaction
                    bodyInsert.CommandText <-
                        "INSERT INTO action_text_rich_texts (body, created_at, name, record_id, record_type, updated_at) " +
                        "VALUES ($body, $now, 'body', $messageId, 'Message', $now)"
                    SqliteDetails.addParameter bodyInsert "$body" (box body)
                    SqliteDetails.addParameter bodyInsert "$now" (box now)
                    SqliteDetails.addParameter bodyInsert "$messageId" (box messageId)
                    bodyInsert.Prepare()
                    bodyInsert.ExecuteNonQuery() |> ignore

                    use touchMessage = connection.CreateCommand()
                    touchMessage.Transaction <- transaction
                    touchMessage.CommandText <- "UPDATE messages SET updated_at = $now WHERE id = $messageId"
                    SqliteDetails.addParameter touchMessage "$now" (box now)
                    SqliteDetails.addParameter touchMessage "$messageId" (box messageId)
                    touchMessage.Prepare()
                    touchMessage.ExecuteNonQuery() |> ignore
                | None -> ()

                use touchRoom = connection.CreateCommand()
                touchRoom.Transaction <- transaction
                touchRoom.CommandText <- "UPDATE rooms SET updated_at = $now WHERE id = $roomId"
                SqliteDetails.addParameter touchRoom "$now" (box now)
                SqliteDetails.addParameter touchRoom "$roomId" (box roomId)
                touchRoom.Prepare()
                touchRoom.ExecuteNonQuery() |> ignore
                transaction.Commit()

                { Id = MessageId messageId
                  RoomId = message.RoomId
                  CreatorId = message.CreatorId
                  ClientMessageId = clientMessageId
                  Body = message.Body
                  CreatedAt = now
                  UpdatedAt = now })
