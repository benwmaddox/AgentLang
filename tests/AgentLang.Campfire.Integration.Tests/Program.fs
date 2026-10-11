namespace AgentLang.Campfire.Integration.Tests

open System
open System.Globalization
open System.IO
open System.Text.Json.Nodes
open Microsoft.Data.Sqlite
open AgentLang
open AgentLang.Campfire.Host
open AgentLang.Campfire.Persistence

[<Sealed>]
type private FixedClock(instant: DateTimeOffset) =
    let mutable calls = 0

    member _.Calls = calls

    interface IClock with
        member _.UtcNow() =
            calls <- calls + 1
            instant

[<Sealed>]
type private ProviderProbe(path: string, clock: FixedClock, provider: HostOperationProvider) =
    let calls = ResizeArray<string * Value list>()
    let mutable disposeCalls = 0

    member _.Path = path
    member _.Clock = clock
    member _.Calls = calls |> Seq.toList
    member _.DisposeCalls = disposeCalls

    member _.AsProvider =
        let invoke operationId values =
            let (HostOperationId name) = operationId
            calls.Add(name, values)
            provider.Invoke operationId values
        let dispose () =
            disposeCalls <- disposeCalls + 1
            provider.Dispose()
        { OperationIds = provider.OperationIds
          Invoke = invoke
          Dispose = dispose }

module Program =
    let mutable private assertions = 0

    let private check condition message =
        assertions <- assertions + 1
        if not condition then failwith message

    let private equal expected actual message =
        assertions <- assertions + 1
        if expected <> actual then
            failwith $"{message}: expected {expected}, got {actual}"

    let private jstr (value: string) = JsonValue.Create(value) :> JsonNode
    let private jint (value: int) = JsonValue.Create(value) :> JsonNode
    let private jbool (value: bool) = JsonValue.Create(value) :> JsonNode

    let private arguments (values: (string * JsonNode) list) =
        let result = JsonObject()
        for name, value in values do
            result[name] <- if isNull value then null else value.DeepClone()
        result

    let private dispatch (engine: Runtime.Engine) operation values =
        engine.Dispatch(operation, arguments values)

    let private expectOk label (response: JsonObject) =
        if not (response.["ok"].GetValue<bool>()) then
            let code =
                try response.["error"].["code"].GetValue<string>()
                with _ -> "unknown"
            let message =
                try response.["error"].["message"].GetValue<string>()
                with _ -> response.ToJsonString()
            failwith $"{label}: expected success, got {code}: {message}"
        response

    let private expectAllPassed label (response: JsonObject) =
        response |> expectOk label |> ignore
        let results = response.["data"].["results"].AsArray()
        for result in results do
            if not (result.["passed"].GetValue<bool>()) then
                let word = result.["word"].GetValue<string>()
                let name = result.["name"].GetValue<string>()
                failwith $"{label}: test {word}/{name} failed: {result.ToJsonString()}"
        results

    let private repositoryRoot = Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, "..", ".."))

    let private applicationDirectory =
        Path.Combine(
            repositoryRoot,
            ".agentlang",
            "campfire-host-236",
            "application",
            "runs",
            DateTimeOffset.UtcNow.ToString("yyyyMMddTHHmmssZ", CultureInfo.InvariantCulture)
                + "-" + Guid.NewGuid().ToString("N"))

    let private runtimeDirectory = Path.Combine(applicationDirectory, "runtime")

    let private readApplicationSource includeRealSqliteTests =
        let app = Path.Combine(repositoryRoot, "applications", "campfire")
        let baseFiles =
            [ Path.Combine(app, "message-policy.flow")
              Path.Combine(app, "room-message.flow")
              Path.Combine(app, "room-message-tests.flow") ]
        let files =
            if includeRealSqliteTests then
                baseFiles @ [ Path.Combine(__SOURCE_DIRECTORY__, "real-provider-tests.flow") ]
            else
                baseFiles
        files |> List.map (fun path -> File.ReadAllText path) |> String.concat "\n\n"

    let private readTypeSource () =
        Path.Combine(repositoryRoot, "applications", "campfire", "campfire-types.flow")
        |> File.ReadAllText

    let private defineFlow (engine: Runtime.Engine) source =
        dispatch engine "define"
            [ "frontend", jstr "flow"
              "syntaxVersion", jint 2
              "source", jstr source ]
        |> expectOk "define the Campfire Flow/2 application"

    let private createProjectDirectory name =
        let path = Path.Combine(applicationDirectory, name)
        Directory.CreateDirectory(path) |> ignore
        path

    let private capabilitySet = Set.ofList [ "db.read"; "db.write"; "clock.read" ]

    let private effectCount (result: JsonNode) effectName =
        let actual = result.["effectAssertion"].["actual"].AsObject()
        let mutable value = Unchecked.defaultof<JsonNode>
        if actual.TryGetPropertyValue(effectName, &value) then value.GetValue<int>() else 0

    let private assertEffectCounts (result: JsonNode) reads writes clocks =
        let caseName = result.["name"].GetValue<string>()
        check (result.["effectAssertion"].["passed"].GetValue<bool>()) "effect assertion passes"
        equal reads (effectCount result "db.read") $"{caseName} database read count"
        equal writes (effectCount result "db.write") $"{caseName} database write count"
        equal clocks (effectCount result "clock.read") $"{caseName} clock read count"

    let private createEngine project env =
        Runtime.Engine(
            project,
            capabilitySet,
            "2026-10-11T12:00:00Z",
            fileSystemMode = FileSystemMode.Virtual,
            testCapabilities = capabilitySet,
            hostOperations = env)

    let private createTypeBootstrapEngine project =
        Runtime.Engine(
            project,
            capabilitySet,
            "2026-10-11T12:00:00Z",
            fileSystemMode = FileSystemMode.Virtual,
            testCapabilities = capabilitySet)

    let private seedNominalTypes project =
        let bootstrap = createTypeBootstrapEngine project
        defineFlow bootstrap (readTypeSource ()) |> ignore
        dispatch bootstrap "commit" []
        |> expectOk "persist Campfire's complete nominal type model before host registration"
        |> ignore

    let private sentinelProvider (calls: int ref) =
        { OperationIds = CampfireHost.operationIds
          Invoke = fun _ _ ->
              calls.Value <- calls.Value + 1
              Error HostOperationFailure.InvocationFailed
          Dispose = ignore }

    let private scopedQualificationAndReload () =
        let productionCalls = ref 0
        let mutable testFactoryCalls = 0
        let production = sentinelProvider productionCalls
        let environment =
            CampfireHost.createEnvironment production (fun () ->
                testFactoryCalls <- testFactoryCalls + 1
                Error HostOperationFailure.TestProviderUnavailable)

        let project = createProjectDirectory "scoped-qualification"
        seedNominalTypes project
        let source = readApplicationSource false
        let engine = createEngine project environment
        defineFlow engine source |> ignore

        let firstRun = dispatch engine "test-all" [] |> expectAllPassed "scoped test suite"
        equal 52 firstRun.Count "scoped suite includes policy, page, list, and create cases"
        let scopedEffects = firstRun |> Seq.filter (fun result -> not (isNull result.["effectAssertion"])) |> Seq.toList
        equal 28 scopedEffects.Length "all 28 operation-backed scoped cases carry effect assertions"
        for result in scopedEffects do assertEffectCounts result 0 0 0
        equal 0 productionCalls.Value "scoped test suite never invokes the production provider"
        equal 0 testFactoryCalls "scoped replacements never construct a real provider"

        for word in
            [ "user.can-administer?"
              "message.can-administer?"
              "campfire.rooms.list-for-user"
              "campfire.messages.list-for-member"
              "campfire.messages.create-for-member" ] do
            dispatch engine "commit" [ "word", jstr word; "library", jbool true ]
            |> expectOk $"qualify {word} as a library word"
            |> ignore

        equal 0 productionCalls.Value "library qualification keeps production operations isolated"
        equal 0 testFactoryCalls "library qualification keeps provider construction lazy"

        let reloaded = createEngine project environment
        let words = dispatch reloaded "words" [] |> expectOk "inspect freshly loaded library words"
        let names =
            words.["data"].["words"].AsArray()
            |> Seq.map (fun word -> word.["name"].GetValue<string>())
            |> Set.ofSeq
        for word in
            [ "user.can-administer?"
              "message.can-administer?"
              "campfire.rooms.list-for-user"
              "campfire.messages.list-for-member"
              "campfire.messages.create-for-member" ] do
            check (names.Contains word) $"fresh runtime loads qualified library word {word}"

        let reloadedRun = dispatch reloaded "test-all" [] |> expectAllPassed "fresh-runtime scoped suite"
        equal 52 reloadedRun.Count "fresh-runtime suite retains all scoped cases"
        let reloadedEffects = reloadedRun |> Seq.filter (fun result -> not (isNull result.["effectAssertion"])) |> Seq.toList
        equal 28 reloadedEffects.Length "reloaded scoped cases retain all effect assertions"
        for result in reloadedEffects do assertEffectCounts result 0 0 0
        equal 0 productionCalls.Value "fresh-runtime library qualification does not use production provider"
        equal 0 testFactoryCalls "fresh-runtime library qualification does not construct providers"

    let private execute (connection: SqliteConnection) sql (parameters: (string * obj) list) =
        use command = connection.CreateCommand()
        command.CommandText <- sql
        for name, value in parameters do
            command.Parameters.AddWithValue(name, value) |> ignore
        command.ExecuteNonQuery()

    let private executeAtPath path sql parameters =
        let builder = SqliteConnectionStringBuilder()
        builder.DataSource <- path
        builder.Mode <- SqliteOpenMode.ReadWrite
        builder.ForeignKeys <- true
        builder.Pooling <- false
        use connection = new SqliteConnection(builder.ToString())
        connection.Open()
        execute connection sql parameters

    let private seedFixture path =
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
        execute connection
            "CREATE TRIGGER reject_test_body BEFORE INSERT ON action_text_rich_texts WHEN NEW.body = 'abort-after-insert' BEGIN SELECT RAISE(ABORT, 'test-only body abort'); END"
            []
        |> ignore

        execute connection "INSERT INTO users(id) VALUES (1), (2), (9)" [] |> ignore
        execute connection
            "INSERT INTO rooms(id, created_at, creator_id, name, type, updated_at) VALUES (1, '2020-01-01 00:00:00', 1, 'Room 1', 'Rooms::Open', '2020-01-01 00:00:00')"
            []
        |> ignore
        execute connection
            "INSERT INTO memberships(room_id, user_id, created_at, updated_at) VALUES (1, 1, '2020-01-01 00:00:00', '2020-01-01 00:00:00'), (99, 9, '2020-01-01 00:00:00', '2020-01-01 00:00:00')"
            []
        |> ignore
        execute connection
            "INSERT INTO messages(id, client_message_id, created_at, creator_id, room_id, updated_at) VALUES (100, 'fixture-message', '2020-01-01 00:00:00', 1, 1, '2020-01-01 00:00:00')"
            []
        |> ignore
        execute connection
            "INSERT INTO action_text_rich_texts(body, created_at, name, record_id, record_type, updated_at) VALUES ('fixture body', '2020-01-01 00:00:00', 'body', 100, 'Message', '2020-01-01 00:00:00'), ('orphan conflict row', '2020-01-01 00:00:00', 'body', 101, 'Message', '2020-01-01 00:00:00')"
            []
        |> ignore

    let private scalarLong path sql parameters =
        let builder = SqliteConnectionStringBuilder()
        builder.DataSource <- path
        builder.Mode <- SqliteOpenMode.ReadWrite
        builder.ForeignKeys <- true
        builder.Pooling <- false
        use connection = new SqliteConnection(builder.ToString())
        connection.Open()
        use command = connection.CreateCommand()
        command.CommandText <- sql
        for name, value in parameters do
            command.Parameters.AddWithValue(name, value) |> ignore
        Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture)

    let private scalarString path sql parameters =
        let builder = SqliteConnectionStringBuilder()
        builder.DataSource <- path
        builder.Mode <- SqliteOpenMode.ReadWrite
        builder.ForeignKeys <- true
        builder.Pooling <- false
        use connection = new SqliteConnection(builder.ToString())
        connection.Open()
        use command = connection.CreateCommand()
        command.CommandText <- sql
        for name, value in parameters do
            command.Parameters.AddWithValue(name, value) |> ignore
        command.ExecuteScalar() |> string

    let private createRealProviderFactory () =
        let probes = ResizeArray<ProviderProbe>()
        let mutable factoryCalls = 0
        let factory () =
            factoryCalls <- factoryCalls + 1
            let path = Path.Combine(runtimeDirectory, $"test-provider-{Guid.NewGuid():N}.db")
            seedFixture path
            let clock = FixedClock(DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero))
            match CampfireHost.createProvider path (clock :> IClock) with
            | Error _ -> Error HostOperationFailure.InvocationFailed
            | Ok provider ->
                let probe = ProviderProbe(path, clock, provider)
                probes.Add probe
                Ok probe.AsProvider
        probes, (fun () -> factoryCalls), factory

    let private adapterContract () =
        equal 4 CampfireHost.manifest.Count "host publishes exactly four fixed operation contracts"
        equal
            CampfireHost.operationIds
            (CampfireHost.manifest |> Map.toSeq |> Seq.map fst |> Set.ofSeq)
            "manifest IDs match provider IDs"
        check
            (CampfireHost.manifest |> Map.forall (fun id definition -> id = definition.Id))
            "manifest keys and embedded IDs agree"

        let path = Path.Combine(runtimeDirectory, $"adapter-contract-{Guid.NewGuid():N}.db")
        seedFixture path
        let clock = FixedClock(DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero))
        let provider =
            match CampfireHost.createProvider path (clock :> IClock) with
            | Ok value -> value
            | Error error -> failwith $"open adapter contract fixture: {error}"

        let roomPageId = HostOperationId "campfire.rooms.page-for-user"
        let malformed =
            provider.Invoke roomPageId
                [ NamedValue("UserId", StringValue "not-an-int64")
                  OptionValue(TNamed "RoomId", None)
                  IntValue 10L ]
        equal (Error HostOperationFailure.ProviderContractMismatch) malformed "adapter rejects malformed nominal values"
        equal
            (Error HostOperationFailure.MissingHandler)
            (provider.Invoke (HostOperationId "campfire.raw-sql") [])
            "adapter rejects unregistered operation IDs"

        match provider.Invoke roomPageId [ NamedValue("UserId", IntValue 1L); OptionValue(TNamed "RoomId", None); IntValue 10L ] with
        | Ok [ ResultValue(TList(TNamed "RoomRow"), TNamed "DbError", Ok(ListValue(TNamed "RoomRow", [ RecordValue("RoomRow", fields) ]))) ] ->
            equal (Some(EnumValue("RoomKind", "open"))) (fields.TryFind "kind") "adapter preserves the closed room kind"
            equal (Some(NamedValue("RoomId", IntValue 1L))) (fields.TryFind "id") "adapter preserves nominal room IDs"
        | other -> failwith $"adapter did not return the declared typed room-page result: {other}"

        match provider.Invoke (HostOperationId "campfire.membership.exists?")
                  [ RecordValue("MembershipKey", Map.ofList [ "user-id", NamedValue("UserId", IntValue 1L); "room-id", NamedValue("RoomId", IntValue 1L) ]) ] with
        | Ok [ ResultValue(TBool, TNamed "DbError", Ok(BoolValue true)) ] ->
            check true "membership result metadata and payload are preserved"
        | other -> failwith $"adapter did not return the declared typed membership result: {other}"
        provider.Dispose()

    let private userIdsInMembershipCalls (probe: ProviderProbe) =
        probe.Calls
        |> List.choose (fun (name, arguments) ->
            if name <> "campfire.membership.exists?" then None
            else
                match arguments with
                | [ RecordValue("MembershipKey", fields) ] ->
                    match fields.TryFind "user-id" with
                    | Some(NamedValue("UserId", IntValue value)) -> Some value
                    | _ -> None
                | _ -> None)

    let private createMessageClientId (probe: ProviderProbe) =
        probe.Calls
        |> List.choose (fun (name, arguments) ->
            if name <> "campfire.messages.create" then None
            else
                match arguments with
                | [ RecordValue("NewMessage", fields) ] ->
                    match fields.TryFind "client-message-id" with
                    | Some(StringValue value) -> Some value
                    | _ -> None
                | _ -> None)
        |> List.tryHead

    let private creatorIdsInMessageCalls (probe: ProviderProbe) =
        probe.Calls
        |> List.choose (fun (name, arguments) ->
            if name <> "campfire.messages.create" then None
            else
                match arguments with
                | [ RecordValue("NewMessage", fields) ] ->
                    match fields.TryFind "creator-id" with
                    | Some(NamedValue("UserId", IntValue value)) -> Some value
                    | _ -> None
                | _ -> None)

    let private resultByName (results: JsonArray) word caseName =
        results
        |> Seq.find (fun result ->
            result.["word"].GetValue<string>() = word
            && result.["name"].GetValue<string>() = caseName)

    let private realSqliteIntegration () =
        let probes, factoryCalls, createTestProvider = createRealProviderFactory ()
        let productionCalls = ref 0
        let productionProvider = sentinelProvider productionCalls
        let environment = CampfireHost.createEnvironment productionProvider createTestProvider
        let project = createProjectDirectory "real-sqlite-tests"
        seedNominalTypes project
        let engine = createEngine project environment
        defineFlow engine (readApplicationSource true) |> ignore

        let results = dispatch engine "test-all" [] |> expectAllPassed "real SQLite integration suite"
        equal 62 results.Count "integration suite includes scoped, policy, and ten real SQLite cases"
        equal 10 (factoryCalls ()) "one lazy test-provider factory call per real attached test"
        equal 0 productionCalls.Value "attached tests use only the disposable test-provider path"

        let expectedRealEffects =
            [ "campfire.rooms.list-for-user", "real-member-page", 1, 0, 0
              "campfire.rooms.list-for-user", "real-invalid-page-size", 1, 0, 0
              "campfire.rooms.list-for-user", "real-missing-user", 1, 0, 0
              "campfire.messages.list-for-member", "real-member-page", 2, 0, 0
              "campfire.messages.list-for-member", "real-non-member-denied", 1, 0, 0
              "campfire.messages.list-for-member", "real-orphan-membership-room-missing", 2, 0, 0
              "campfire.messages.create-for-member", "real-member-create", 1, 1, 1
              "campfire.messages.create-for-member", "real-non-member-does-not-create", 1, 0, 0
              "campfire.messages.create-for-member", "real-body-failure-rolls-back", 1, 1, 1
              "campfire.messages.create-for-member", "real-unique-conflict-rolls-back", 1, 1, 1 ]

        for word, caseName, reads, writes, clocks in expectedRealEffects do
            resultByName results word caseName |> fun result -> assertEffectCounts result reads writes clocks

        let allProbes = probes |> Seq.toList
        equal 10 allProbes.Length "all real tests got distinct provider probes"
        equal 10 (allProbes |> List.map (fun probe -> probe.Path) |> Set.ofList |> Set.count) "each test used a distinct fixture database"
        for probe in allProbes do
            let providerName = Path.GetFileName(probe.Path)
            equal 1 probe.DisposeCalls $"provider from {providerName} is disposed once"

        let aggregatedCalls =
            allProbes
            |> List.collect (fun probe -> probe.Calls)
            |> List.countBy fst
            |> Map.ofList
        equal 3 (aggregatedCalls.["campfire.rooms.page-for-user"]) "real room pages use the bounded page operation"
        equal 7 (aggregatedCalls.["campfire.membership.exists?"]) "each member-scoped operation checks exact membership"
        equal 2 (aggregatedCalls.["campfire.messages.last-page"]) "only two authorized message reads reach last-page"
        equal 3 (aggregatedCalls.["campfire.messages.create"]) "only three authorized message creates reach persistence"
        equal 15 (aggregatedCalls |> Map.toSeq |> Seq.sumBy snd) "real suite makes only the four registered fixed operations"
        equal 3 (allProbes |> List.sumBy (fun probe -> probe.Clock.Calls)) "clock is read only by actual message-create attempts"
        let createProbes = allProbes |> List.filter (fun probe -> createMessageClientId probe |> Option.isSome)
        equal 3 createProbes.Length "all successful-member create attempts reach the host adapter"
        for probe in createProbes do
            equal [ 1L ] (creatorIdsInMessageCalls probe) "Flow derives the persisted creator from the supplied authenticated caller"

        let deniedProbes = allProbes |> List.filter (fun probe -> userIdsInMembershipCalls probe = [ 2L ])
        equal 2 deniedProbes.Length "the two denied cases still perform their membership lookup"
        for probe in deniedProbes do
            let names = probe.Calls |> List.map fst
            equal [ "campfire.membership.exists?" ] names "denied calls stop before last-page or message-create"

        let abortedProbe = allProbes |> List.find (fun probe -> createMessageClientId probe = Some "real-abort-create")
        equal 0L (scalarLong abortedProbe.Path "SELECT COUNT(*) FROM messages WHERE client_message_id = $id" [ "$id", box "real-abort-create" ]) "body-trigger failure rolls back message row"
        equal 0L (scalarLong abortedProbe.Path "SELECT COUNT(*) FROM action_text_rich_texts WHERE body = $body" [ "$body", box "abort-after-insert" ]) "body-trigger failure leaves no rich-text row"
        equal "2020-01-01 00:00:00" (scalarString abortedProbe.Path "SELECT updated_at FROM rooms WHERE id = 1" []) "body-trigger failure does not touch room"

        let conflictProbe = allProbes |> List.find (fun probe -> createMessageClientId probe = Some "real-unique-create")
        equal 0L (scalarLong conflictProbe.Path "SELECT COUNT(*) FROM messages WHERE client_message_id = $id" [ "$id", box "real-unique-create" ]) "unique conflict rolls back message row"
        equal "2020-01-01 00:00:00" (scalarString conflictProbe.Path "SELECT updated_at FROM rooms WHERE id = 1" []) "unique conflict does not touch room"

        let bodylessProbe = allProbes |> List.find (fun probe -> createMessageClientId probe = Some "real-bodyless-create")
        equal 1L (scalarLong bodylessProbe.Path "SELECT COUNT(*) FROM messages WHERE client_message_id = $id" [ "$id", box "real-bodyless-create" ]) "bodyless create persists after provider scope closes"
        equal 2L (scalarLong bodylessProbe.Path "SELECT COUNT(*) FROM action_text_rich_texts WHERE record_type = 'Message' AND name = 'body'" []) "bodyless create adds no rich-text row to the two-row fixture"
        equal "2025-01-01 00:00:00" (scalarString bodylessProbe.Path "SELECT updated_at FROM rooms WHERE id = 1" []) "bodyless create still touches its room"
        bodylessProbe.Path

    let private reopenedMessageRead databasePath =
        // The conflict fixture's orphan row targets the next autoincrement ID. Remove
        // only that test sentinel so the reopened read sees the bodyless create itself.
        executeAtPath databasePath
            "DELETE FROM action_text_rich_texts WHERE record_id = 101 AND record_type = 'Message' AND name = 'body' AND body = 'orphan conflict row'"
            []
        |> ignore

        let clock = FixedClock(DateTimeOffset(2025, 1, 2, 0, 0, 0, TimeSpan.Zero))
        let underlying =
            match CampfireHost.createProvider databasePath (clock :> IClock) with
            | Ok provider -> provider
            | Error error -> failwith $"reopen attached-test database through host adapter: {error}"
        let probe = ProviderProbe(databasePath, clock, underlying)
        let mutable testFactoryCalls = 0
        let environment =
            CampfireHost.createEnvironment probe.AsProvider (fun () ->
                testFactoryCalls <- testFactoryCalls + 1
                Error HostOperationFailure.TestProviderUnavailable)
        let project = createProjectDirectory "reopened-message-read"
        seedNominalTypes project
        let engine = createEngine project environment
        defineFlow engine (readApplicationSource false) |> ignore
        let response =
            dispatch engine "eval"
                [ "frontend", jstr "flow"
                  "syntaxVersion", jint 2
                  "structured", jbool true
                  "code", jstr "campfire.messages.list-for-member(UserId.new(1), RoomId.new(1))" ]
            |> expectOk "read the created message through a fresh host provider"

        let structured = response.["data"].["structuredStack"].["values"].AsArray()
        equal 1 structured.Count "reopened read returns one typed result"
        equal "ok" (structured[0].["case"].GetValue<string>()) "reopened message read is authorized and succeeds"
        let rows = structured[0].["value"].["items"].AsArray()
        equal 2 rows.Count "reopened message page includes the fixture and persisted create"
        let created = rows[1]
        let createdField name =
            created.["fields"].AsArray()
            |> Seq.find (fun field -> field.["name"].GetValue<string>() = name)
        let createdClientMessageId = createdField "client-message-id"
        equal
            "real-bodyless-create"
            (createdClientMessageId.["value"].["value"].GetValue<string>())
            "reopened Flow read returns the persisted client message ID"
        let createdBody = createdField "body"
        equal "none" (createdBody.["value"].["case"].GetValue<string>()) "reopened bodyless message remains bodyless"
        let createdCreator = createdField "creator-id"
        equal
            "1"
            (createdCreator.["value"].["value"].["value"].GetValue<string>())
            "reopened row preserves its caller-derived creator ID"
        equal
            [ "campfire.membership.exists?"; "campfire.messages.last-page" ]
            (probe.Calls |> List.map fst)
            "fresh Flow read checks membership before the fixed message-page operation"
        equal 0 clock.Calls "reopened read does not consume the create clock"
        equal 0 testFactoryCalls "normal reopened read never constructs a test provider"
        equal 2 (response.["data"].["effects"].["db.read"].GetValue<int>()) "reopened Flow read reports both actual database reads"
        equal 0 probe.DisposeCalls "Engine preserves the host-owned reopened provider"
        let hostOwnedProvider = probe.AsProvider
        hostOwnedProvider.Dispose()
        equal 1 probe.DisposeCalls "host owner disposes the reopened provider after inspection"

    let private productionProviderCall () =
        let path = Path.Combine(runtimeDirectory, $"production-provider-{Guid.NewGuid():N}.db")
        seedFixture path
        let clock = FixedClock(DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero))
        let underlying =
            match CampfireHost.createProvider path (clock :> IClock) with
            | Ok provider -> provider
            | Error error -> failwith $"create explicit production provider: {error}"
        let productionProbe = ProviderProbe(path, clock, underlying)
        let mutable testFactoryCalls = 0
        let environment =
            CampfireHost.createEnvironment productionProbe.AsProvider (fun () ->
                testFactoryCalls <- testFactoryCalls + 1
                Error HostOperationFailure.TestProviderUnavailable)
        let project = createProjectDirectory "production-provider"
        seedNominalTypes project
        let engine = createEngine project environment
        defineFlow engine (readApplicationSource false) |> ignore
        let evaluated =
            dispatch engine "eval"
                [ "frontend", jstr "flow"
                  "syntaxVersion", jint 2
                  "structured", jbool true
                  "code", jstr "campfire.messages.create-for-member(UserId.new(1), RoomId.new(1), \"production-bodyless\", option.none<String>())" ]
            |> expectOk "call the explicit production provider through the Flow application"

        let stack = evaluated.["data"].["structuredStack"].["values"].AsArray()
        equal 1 stack.Count "production call returns one typed Flow value"
        equal "result" (stack[0].["kind"].GetValue<string>()) "production result keeps Result metadata"
        equal "ok" (stack[0].["case"].GetValue<string>()) "production create returns an application success"
        let creatorField =
            stack[0].["value"].["fields"].AsArray()
            |> Seq.find (fun field -> field.["name"].GetValue<string>() = "creator-id")
        equal "UserId" (creatorField.["value"].["name"].GetValue<string>()) "host adapter retains the creator's nominal ID"
        equal "1" (creatorField.["value"].["value"].["value"].GetValue<string>()) "the authenticated caller supplies the persisted creator"
        equal
            [ "campfire.membership.exists?"; "campfire.messages.create" ]
            (productionProbe.Calls |> List.map fst)
            "production wrapper performs membership before one fixed create operation"
        equal 1 clock.Calls "production creation reads the injected host clock once"
        equal 0 testFactoryCalls "normal evaluation uses no test provider factory"
        equal 1L (scalarLong path "SELECT COUNT(*) FROM messages WHERE client_message_id = $id AND creator_id = 1" [ "$id", box "production-bodyless" ]) "production message persists for the supplied caller"
        equal 2L (scalarLong path "SELECT COUNT(*) FROM action_text_rich_texts WHERE record_type = 'Message' AND name = 'body'" []) "production bodyless create adds no rich-text row"
        equal 0 productionProbe.DisposeCalls "Engine does not dispose a host-owned production provider"
        let hostOwnedProductionProvider = productionProbe.AsProvider
        hostOwnedProductionProvider.Dispose()
        equal 1 productionProbe.DisposeCalls "host owner can explicitly release its production provider"

    [<EntryPoint>]
    let main _ =
        try
            Directory.CreateDirectory(runtimeDirectory) |> ignore
            Environment.SetEnvironmentVariable("TEMP", runtimeDirectory)
            Environment.SetEnvironmentVariable("TMP", runtimeDirectory)

            adapterContract ()
            scopedQualificationAndReload ()
            let persistedMessageDatabase = realSqliteIntegration ()
            reopenedMessageRead persistedMessageDatabase
            productionProviderCall ()
            printfn "PASS Campfire Host integration: %d assertions" assertions
            0
        with error ->
            eprintfn "FAIL Campfire Host integration after %d assertions: %s" assertions error.Message
            eprintfn "%s" error.StackTrace
            1
