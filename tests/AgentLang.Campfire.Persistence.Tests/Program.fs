namespace AgentLang.Campfire.Persistence.Tests

open System
open System.Globalization
open System.IO
open Microsoft.Data.Sqlite
open AgentLang.Campfire.Persistence

[<Sealed>]
type private FixedClock(initial: DateTimeOffset) =
    let mutable current = initial

    member _.Set(value: DateTimeOffset) = current <- value

    interface IClock with
        member _.UtcNow() = current

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
        Path.GetFullPath(
            Path.Combine(
                __SOURCE_DIRECTORY__,
                "..",
                "..",
                ".agentlang",
                "campfire-235",
                "implementation",
                "test-data"))

    let private createFixture label seed =
        Directory.CreateDirectory(evidenceDirectory) |> ignore
        let path =
            Path.Combine(
                evidenceDirectory,
                $"{label}-{DateTime.UtcNow:yyyyMMddTHHmmssfffZ}-{Guid.NewGuid():N}.db")
        let builder = SqliteConnectionStringBuilder()
        builder.DataSource <- path
        builder.Mode <- SqliteOpenMode.ReadWriteCreate
        builder.ForeignKeys <- true
        builder.Pooling <- false
        use connection = new SqliteConnection(builder.ToString())
        connection.Open()
        use command = connection.CreateCommand()
        command.CommandText <-
            "CREATE TABLE users (id INTEGER PRIMARY KEY AUTOINCREMENT); " +
            "CREATE TABLE rooms (id INTEGER PRIMARY KEY AUTOINCREMENT, created_at datetime NOT NULL, creator_id bigint NOT NULL, name varchar, type varchar NOT NULL, updated_at datetime NOT NULL); " +
            "CREATE TABLE memberships (id INTEGER PRIMARY KEY AUTOINCREMENT, connected_at datetime, connections integer NOT NULL DEFAULT 0, created_at datetime NOT NULL, involvement varchar DEFAULT 'mentions', room_id integer NOT NULL, unread_at datetime, updated_at datetime NOT NULL, user_id integer NOT NULL); " +
            "CREATE UNIQUE INDEX index_memberships_on_room_id_and_user_id ON memberships(room_id, user_id); " +
            "CREATE TABLE messages (id INTEGER PRIMARY KEY AUTOINCREMENT, client_message_id varchar NOT NULL, created_at datetime NOT NULL, creator_id integer NOT NULL, room_id integer NOT NULL, updated_at datetime NOT NULL, FOREIGN KEY(room_id) REFERENCES rooms(id), FOREIGN KEY(creator_id) REFERENCES users(id)); " +
            "CREATE TABLE action_text_rich_texts (id INTEGER PRIMARY KEY AUTOINCREMENT, body text, created_at datetime NOT NULL, name varchar NOT NULL, record_id bigint NOT NULL, record_type varchar NOT NULL, updated_at datetime NOT NULL); " +
            "CREATE UNIQUE INDEX index_action_text_rich_texts_uniqueness ON action_text_rich_texts(record_type, record_id, name);"
        command.ExecuteNonQuery() |> ignore
        seed connection
        path

    let private execute (connection: SqliteConnection) (sql: string) (parameters: (string * obj) list) =
        use command = connection.CreateCommand()
        command.CommandText <- sql
        for name, value in parameters do
            command.Parameters.AddWithValue(name, value) |> ignore
        command.ExecuteNonQuery()

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
        command.ExecuteScalar() |> string

    let private scalarLongAtPath path sql =
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

    let private upstreamLastPageIds path roomId =
        let builder = SqliteConnectionStringBuilder()
        builder.DataSource <- path
        builder.Mode <- SqliteOpenMode.ReadWrite
        builder.ForeignKeys <- true
        builder.Pooling <- false
        use connection = new SqliteConnection(builder.ToString())
        connection.Open()
        use command = connection.CreateCommand()
        command.CommandText <-
            "SELECT id FROM messages WHERE room_id = $roomId ORDER BY created_at DESC LIMIT 40"
        command.Parameters.AddWithValue("$roomId", box roomId) |> ignore
        use reader = command.ExecuteReader()
        let descending = ResizeArray<int64>()
        while reader.Read() do
            descending.Add(reader.GetInt64(0))
        descending |> Seq.toList |> List.rev

    let private seedUser connection userId =
        execute connection "INSERT INTO users(id) VALUES ($id)" [ "$id", box userId ] |> ignore

    let private seedRoom connection roomId userId kind createdAt updatedAt =
        execute connection
            "INSERT INTO rooms(id, created_at, creator_id, name, type, updated_at) VALUES ($id, $created, $creator, $name, $type, $updated)"
            [ "$id", box roomId
              "$created", box createdAt
              "$creator", box userId
              "$name", box $"Room {roomId}"
              "$type", box kind
              "$updated", box updatedAt ]
        |> ignore

    let private seedMembership connection userId roomId =
        execute connection
            "INSERT INTO memberships(room_id, user_id, created_at, updated_at) VALUES ($room, $user, '2020-01-01 00:00:00', '2020-01-01 00:00:00')"
            [ "$room", box roomId; "$user", box userId ]
        |> ignore

    let private openStore path clock =
        match SqliteProvider.OpenExisting(path, clock) with
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

    let private roomPagesAndMembership () =
        let path =
            createFixture "room-pages" (fun connection ->
                seedUser connection 1L
                seedUser connection 2L
                for roomId in 1L .. 106L do
                    let kind =
                        match roomId % 3L with
                        | 1L -> "Rooms::Open"
                        | 2L -> "Rooms::Closed"
                        | _ -> "Rooms::Direct"
                    let kind = if roomId = 106L then "Rooms::Unsupported" else kind
                    seedRoom connection roomId 1L kind "2020-01-01 00:00:00" "2020-01-01 00:00:00"
                    seedMembership connection 1L roomId
                seedMembership connection 999L 999L)

        let clock = FixedClock(DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero))
        let store = openStore path (clock :> IClock)
        let firstPage =
            store.PageRoomsForUser(UserId 1L, None, 100L)
            |> expectOk "first 100 room rows"
        equal 100 firstPage.Length "room page is bounded at 100"
        equal [ 1L .. 100L ] (firstPage |> List.map (fun row -> let (RoomId id) = row.Id in id)) "room page ordered by id"
        equal Open firstPage.Head.Kind "known STI maps to the closed room kind"
        equal Closed firstPage.Tail.Head.Kind "closed STI maps to the closed room kind"
        equal Direct firstPage.Tail.Tail.Head.Kind "direct STI maps to the closed room kind"
        let singleRow = store.PageRoomsForUser(UserId 1L, None, 1L) |> expectOk "one-row room page"
        equal [ 1L ] (singleRow |> List.map (fun row -> let (RoomId id) = row.Id in id)) "page size one is honored"

        let nextPage =
            store.PageRoomsForUser(UserId 1L, Some(RoomId 100L), 5L)
            |> expectOk "room keyset continuation"
        equal [ 101L; 102L; 103L; 104L; 105L ] (nextPage |> List.map (fun row -> let (RoomId id) = row.Id in id)) "keyset starts after cursor"
        store.PageRoomsForUser(UserId 1L, Some(RoomId 105L), 10L)
        |> expectError Unavailable "unknown STI value fails closed"
        store.PageRoomsForUser(UserId 2L, None, 10L)
        |> expectOk "existing user with no rooms"
        |> fun rows -> equal [] rows "existing user with no rooms returns empty list"
        store.PageRoomsForUser(UserId 999L, None, 10L)
        |> expectError NotFound "missing user page"
        store.PageRoomsForUser(UserId 1L, None, 0L)
        |> expectError InvalidInput "zero room page size"
        store.PageRoomsForUser(UserId 1L, None, 101L)
        |> expectError InvalidInput "room page size over cap"

        store.MembershipExists { UserId = UserId 1L; RoomId = RoomId 1L }
        |> expectOk "existing membership"
        |> fun exists -> check exists "existing membership returns true"
        store.MembershipExists { UserId = UserId 2L; RoomId = RoomId 1L }
        |> expectOk "absent membership"
        |> fun exists -> check (not exists) "absent membership returns false"
        store.MembershipExists { UserId = UserId 999L; RoomId = RoomId 999L }
        |> expectOk "membership row without foreign keys"
        |> fun exists -> check exists "orphan membership row remains visible"

    let private seedMessage connection roomId creatorId messageId clientId createdAt body =
        execute connection
            "INSERT INTO messages(id, client_message_id, created_at, creator_id, room_id, updated_at) VALUES ($id, $client, $created, $creator, $room, $created)"
            [ "$id", box messageId
              "$client", box clientId
              "$created", box createdAt
              "$creator", box creatorId
              "$room", box roomId ]
        |> ignore
        match body with
        | Some text ->
            execute connection
                "INSERT INTO action_text_rich_texts(body, created_at, name, record_id, record_type, updated_at) VALUES ($body, $created, 'body', $record, 'Message', $created)"
                [ "$body", box text
                  "$created", box createdAt
                  "$record", box messageId ]
            |> ignore
        | None -> ()

    let private messagePage () =
        let path =
            createFixture "message-page" (fun connection ->
                seedUser connection 1L
                seedRoom connection 1L 1L "Rooms::Open" "2020-01-01 00:00:00" "2020-01-01 00:00:00"
                seedRoom connection 2L 1L "Rooms::Closed" "2020-01-01 00:00:00" "2020-01-01 00:00:00"
                for messageId in 1L .. 41L do
                    let second = int (messageId - 1L)
                    let createdAt =
                        if messageId = 2L || messageId = 3L then "2025-01-01 00:00:01.000000"
                        else $"2025-01-01 00:00:{second:D2}.000000"
                    let body = if messageId = 41L then Some "last-body" elif messageId = 2L then Some "first-visible-body" else None
                    seedMessage connection 1L 1L messageId $"client-{messageId}" createdAt body)

        let clock = FixedClock(DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero))
        let store = openStore path (clock :> IClock)
        let page = store.LastPage(RoomId 1L) |> expectOk "message last page"
        equal 40 page.Length "message page has at most forty rows"
        let actualIds = page |> List.map (fun row -> let (MessageId id) = row.Id in id)
        equal [ 2L .. 41L ] (List.sort actualIds) "newest forty are returned oldest-first"
        equal (upstreamLastPageIds path 1L) actualIds "equal timestamps follow upstream DESC query then reverse"
        equal (Some "first-visible-body") (page |> List.find (fun row -> row.Id = MessageId 2L)).Body "message body is read from Action Text"
        equal (Some "last-body") (List.last page).Body "newest message body is read from Action Text"
        equal None (List.item 38 page).Body "missing rich-text body remains optional"
        store.LastPage(RoomId 2L)
        |> expectOk "empty existing room message page"
        |> fun rows -> equal [] rows "empty room has no messages"
        store.LastPage(RoomId 999L) |> expectError NotFound "missing room message page"

    let private createAndReopen () =
        let path =
            createFixture "message-create" (fun connection ->
                seedUser connection 1L
                seedRoom connection 1L 1L "Rooms::Open" "2000-01-01 00:00:00" "2000-01-01 00:00:00"
                execute connection "CREATE TABLE touch_audit (kind text NOT NULL)" [] |> ignore
                execute connection
                    "CREATE TRIGGER audit_message_touch AFTER UPDATE OF updated_at ON messages BEGIN INSERT INTO touch_audit(kind) VALUES ('message'); END"
                    []
                |> ignore
                execute connection
                    "CREATE TRIGGER audit_room_touch AFTER UPDATE OF updated_at ON rooms BEGIN INSERT INTO touch_audit(kind) VALUES ('room'); END"
                    []
                |> ignore)

        let clock = FixedClock(DateTimeOffset(2025, 1, 2, 3, 4, 5, TimeSpan.FromHours(2)).AddTicks(1_234_567L))
        let firstTime = "2025-01-02 01:04:05.123456"
        let clientId = "client'); DROP TABLE messages;--"
        let body = "<script>x'; DROP TABLE rooms;--</script>"
        let firstStore = openStore path (clock :> IClock)
        let first =
            firstStore.CreateMessage
                { RoomId = RoomId 1L
                  CreatorId = UserId 1L
                  ClientMessageId = clientId
                  Body = Some body }
            |> expectOk "create message with body"
        equal (MessageId 1L) first.Id "first message receives generated database ID"
        equal (Some body) first.Body "create returns supplied body"
        equal firstTime first.CreatedAt "timestamp is UTC and truncated to microseconds"
        equal firstTime first.UpdatedAt "body insert touches message"
        equal 1L (scalarLongAtPath path "SELECT COUNT(*) FROM touch_audit WHERE kind = 'message'") "body insert causes one message touch"
        equal 1L (scalarLongAtPath path "SELECT COUNT(*) FROM touch_audit WHERE kind = 'room'") "message create touches its room"

        clock.Set(DateTimeOffset(2025, 1, 2, 1, 4, 6, TimeSpan.Zero))
        let second =
            firstStore.CreateMessage
                { RoomId = RoomId 1L
                  CreatorId = UserId 1L
                  ClientMessageId = clientId
                  Body = None }
            |> expectOk "create bodyless message with duplicate client ID"
        equal (MessageId 2L) second.Id "second message receives database ID"
        equal None second.Body "bodyless message has no Action Text row"
        equal "2025-01-02 01:04:06" second.CreatedAt "whole-second timestamp omits fractional part"
        equal second.CreatedAt second.UpdatedAt "bodyless message is not separately touched"
        equal 1L (scalarLongAtPath path "SELECT COUNT(*) FROM touch_audit WHERE kind = 'message'") "bodyless create does not touch its message again"
        equal 2L (scalarLongAtPath path "SELECT COUNT(*) FROM touch_audit WHERE kind = 'room'") "bodyless create still touches its room"

        equal second.CreatedAt (scalarString path "SELECT updated_at FROM rooms WHERE id = 1") "room is touched even when body is absent"

        let reopened = openStore path (clock :> IClock)
        let persisted = reopened.LastPage(RoomId 1L) |> expectOk "reopen and read created messages"
        equal 2 persisted.Length "message rows persist after provider reopen"
        equal clientId persisted.Head.ClientMessageId "client IDs remain stored as data"
        equal (Some body) persisted.Head.Body "injection-shaped body remains stored as data"
        equal None (List.last persisted).Body "bodyless create remains bodyless"
        equal 2L (scalarLongAtPath path "SELECT COUNT(*) FROM messages") "duplicate client IDs are not unique"
        equal 1L (scalarLongAtPath path "SELECT COUNT(*) FROM action_text_rich_texts") "only body-bearing message has Action Text"

        reopened.CreateMessage
            { RoomId = RoomId 1L
              CreatorId = UserId 1L
              ClientMessageId = null
              Body = None }
        |> expectError InvalidInput "null client ID rejected as typed input error"
        reopened.CreateMessage
            { RoomId = RoomId 1L
              CreatorId = UserId 1L
              ClientMessageId = "null-body"
              Body = Some null }
        |> expectError InvalidInput "null body rejected as typed input error"

    let private typedErrorsAndRollback () =
        let path =
            createFixture "typed-errors" (fun connection ->
                seedUser connection 1L
                seedRoom connection 1L 1L "Rooms::Open" "2000-01-01 00:00:00" "2000-01-01 00:00:00")
        let clock = FixedClock(DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero))
        let store = openStore path (clock :> IClock)
        store.CreateMessage
            { RoomId = RoomId 999L
              CreatorId = UserId 1L
              ClientMessageId = "missing-room"
              Body = None }
        |> expectError NotFound "foreign key missing room"
        store.CreateMessage
            { RoomId = RoomId 1L
              CreatorId = UserId 999L
              ClientMessageId = "missing-user"
              Body = None }
        |> expectError NotFound "foreign key missing creator"
        let missingPath = Path.Combine(evidenceDirectory, $"missing-{Guid.NewGuid():N}.db")
        SqliteProvider.OpenExisting(missingPath, clock :> IClock)
        |> expectError Unavailable "nonexistent database cannot be opened"
        check (not (File.Exists(missingPath))) "open existing does not create a database"

        let conflictPath =
            createFixture "unique-conflict" (fun connection ->
                seedUser connection 1L
                seedRoom connection 1L 1L "Rooms::Open" "2000-01-01 00:00:00" "2000-01-01 00:00:00"
                execute connection
                    "INSERT INTO action_text_rich_texts(body, created_at, name, record_id, record_type, updated_at) VALUES ('orphan', '2020-01-01 00:00:00', 'body', 1, 'Message', '2020-01-01 00:00:00')"
                    []
                |> ignore)
        let conflictStore = openStore conflictPath (clock :> IClock)
        conflictStore.CreateMessage
            { RoomId = RoomId 1L
              CreatorId = UserId 1L
              ClientMessageId = "unique-conflict"
              Body = Some "body" }
        |> expectError ConstraintConflict "rich-text unique index maps to conflict"
        equal 0L (scalarLongAtPath conflictPath "SELECT COUNT(*) FROM messages") "unique failure rolls message back"
        equal 1L (scalarLongAtPath conflictPath "SELECT COUNT(*) FROM action_text_rich_texts") "orphan rich text remains unchanged"
        equal "2000-01-01 00:00:00" (scalarString conflictPath "SELECT updated_at FROM rooms WHERE id = 1") "unique failure does not touch room"

        let triggerPath =
            createFixture "trigger-rollback" (fun connection ->
                seedUser connection 1L
                seedRoom connection 1L 1L "Rooms::Open" "2000-01-01 00:00:00" "2000-01-01 00:00:00"
                execute connection
                    "CREATE TRIGGER reject_body BEFORE INSERT ON action_text_rich_texts BEGIN SELECT RAISE(ABORT, 'test-only abort'); END"
                    []
                |> ignore)
        let triggerStore = openStore triggerPath (clock :> IClock)
        triggerStore.CreateMessage
            { RoomId = RoomId 1L
              CreatorId = UserId 1L
              ClientMessageId = "trigger-abort"
              Body = Some "body" }
        |> expectError Unavailable "trigger abort is unavailable"
        equal 0L (scalarLongAtPath triggerPath "SELECT COUNT(*) FROM messages") "trigger failure rolls message back"
        equal 0L (scalarLongAtPath triggerPath "SELECT COUNT(*) FROM action_text_rich_texts") "trigger failure leaves no rich-text row"
        equal "2000-01-01 00:00:00" (scalarString triggerPath "SELECT updated_at FROM rooms WHERE id = 1") "body trigger failure does not touch room"

        let roomTouchPath =
            createFixture "room-touch-rollback" (fun connection ->
                seedUser connection 1L
                seedRoom connection 1L 1L "Rooms::Open" "2000-01-01 00:00:00" "2000-01-01 00:00:00"
                execute connection
                    "CREATE TRIGGER reject_room_touch BEFORE UPDATE OF updated_at ON rooms BEGIN SELECT RAISE(ABORT, 'test-only room touch abort'); END"
                    []
                |> ignore)
        let roomTouchStore = openStore roomTouchPath (clock :> IClock)
        roomTouchStore.CreateMessage
            { RoomId = RoomId 1L
              CreatorId = UserId 1L
              ClientMessageId = "room-touch-abort"
              Body = Some "body" }
        |> expectError Unavailable "room touch abort is unavailable"
        equal 0L (scalarLongAtPath roomTouchPath "SELECT COUNT(*) FROM messages") "room touch abort rolls message back"
        equal 0L (scalarLongAtPath roomTouchPath "SELECT COUNT(*) FROM action_text_rich_texts") "room touch abort rolls rich-text back"
        equal "2000-01-01 00:00:00" (scalarString roomTouchPath "SELECT updated_at FROM rooms WHERE id = 1") "room touch abort leaves room unchanged"

    [<EntryPoint>]
    let main _ =
        try
            assertGroup "room paging and membership" roomPagesAndMembership
            assertGroup "last message page" messagePage
            assertGroup "message create and reopen" createAndReopen
            assertGroup "typed errors and transaction rollback" typedErrorsAndRollback
            printfn "PASS %d groups, %d assertions" groups assertions
            0
        with error ->
            eprintfn "FAIL after %d groups and %d assertions: %s" groups assertions error.Message
            eprintfn "%s" error.StackTrace
            1
