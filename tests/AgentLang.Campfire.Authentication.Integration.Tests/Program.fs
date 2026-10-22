namespace AgentLang.Campfire.Authentication.Integration.Tests

open System
open System.Globalization
open System.IO
open System.Security.Cryptography
open System.Text
open System.Text.Json
open System.Text.Json.Nodes
open Microsoft.Data.Sqlite
open AgentLang
open AgentLang.Campfire.Authentication.Host
open AgentLang.Campfire.Cookies

[<Sealed>]
type private ProviderProbe(path: string, provider: HostOperationProvider) =
    let calls = ResizeArray<string * Value list>()
    let mutable disposeCalls = 0

    member _.Path = path
    member _.Calls = calls |> Seq.toList
    member _.DisposeCalls = disposeCalls

    member _.AsProvider =
        let invoke operationId values =
            let (HostOperationId name) = operationId
            calls.Add(name, values)
            let injectedUserFailure =
                name = "campfire.users.find-by-id"
                && (match calls |> Seq.tryHead with
                    | Some("campfire.sessions.find-by-token", [ NamedValue("SessionToken", StringValue "user-error-after-refresh-token") ]) -> true
                    | _ -> false)
            if injectedUserFailure then
                Ok [ ResultValue(TOption(TNamed "AuthUser"), TNamed "DbError", Error(EnumValue("DbError", "unavailable", None))) ]
            else
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
        if expected <> actual then failwith $"{message}: expected {expected}, got {actual}"

    let private repositoryRoot = Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, "..", ".."))
    let private privateRoot = Path.Combine(repositoryRoot, ".agentlang", "campfire-http-242", "auth-order")
    let private runDirectory =
        Path.Combine(privateRoot, "runs", $"{DateTime.UtcNow:yyyyMMddTHHmmssfffZ}-{Guid.NewGuid():N}")
    let private runtimeDirectory = Path.Combine(runDirectory, "runtime")
    let private projectDirectory = Path.Combine(runDirectory, "project")
    let private capabilitySet = Set.ofList [ "db.read"; "db.write"; "clock.read" ]

    let private jstr (value: string) = JsonValue.Create(value) :> JsonNode
    let private jint (value: int) = JsonValue.Create(value) :> JsonNode
    let private jbool (value: bool) = JsonValue.Create(value) :> JsonNode

    let private arguments (values: (string * JsonNode) list) =
        let result = JsonObject()
        for name, value in values do result[name] <- if isNull value then null else value.DeepClone()
        result

    let private dispatch (engine: Runtime.Engine) operation values =
        engine.Dispatch(operation, arguments values)

    let private expectOk label (response: JsonObject) =
        if not (response["ok"].GetValue<bool>()) then
            let code =
                try (response["error"]["code"]).GetValue<string>()
                with _ -> "unknown"
            let message =
                try (response["error"]["message"]).GetValue<string>()
                with _ -> response.ToJsonString()
            failwith $"{label}: expected success, got {code}: {message}"
        response

    let private expectAllPassed label (response: JsonObject) =
        response |> expectOk label |> ignore
        let results = (response["data"]["results"]).AsArray()
        for result in results do
            if not (result["passed"].GetValue<bool>()) then
                let word = result["word"].GetValue<string>()
                let name = result["name"].GetValue<string>()
                failwith $"{label}: test {word}/{name} failed: {result.ToJsonString()}"
        results

    let private readFile relativePath = Path.Combine(repositoryRoot, relativePath) |> File.ReadAllText

    let private readSeedTypeSource () =
        [ readFile "applications/campfire/campfire-types.flow"
          readFile "applications/campfire/session-policy.flow"
          readFile "applications/campfire/authentication-types.flow" ]
        |> String.concat "\n\n"

    let private readApplicationSource (verifiedToken: string) (includeRealTests: bool) =
        let baseFiles =
            [ readFile "applications/campfire/authentication.flow"
              readFile "applications/campfire/authentication-tests.flow" ]
        let sourceFiles =
            if includeRealTests then
                baseFiles @ [ readFile "tests/AgentLang.Campfire.Authentication.Integration.Tests/real-authentication-tests.flow" ]
            else baseFiles
        let escapedToken = verifiedToken.Replace("\\", "\\\\").Replace("\"", "\\\"")
        sourceFiles
        |> String.concat "\n\n"
        |> fun source -> source.Replace("{{VERIFIED_SESSION_TOKEN}}", escapedToken, StringComparison.Ordinal)

    let private defineFlow (engine: Runtime.Engine) source =
        dispatch engine "define"
            [ "frontend", jstr "flow"
              "syntaxVersion", jint 2
              "source", jstr source ]
        |> expectOk "define Campfire's Flow/2 authentication application"

    let private createEngine project environment =
        Runtime.Engine(
            project,
            capabilitySet,
            "2026-10-11T12:00:00Z",
            fileSystemMode = FileSystemMode.Virtual,
            testCapabilities = capabilitySet,
            hostOperations = environment)

    let private createTypeBootstrapEngine project =
        Runtime.Engine(
            project,
            capabilitySet,
            "2026-10-11T12:00:00Z",
            fileSystemMode = FileSystemMode.Virtual,
            testCapabilities = capabilitySet)

    let private seedTypesWithoutHost project =
        let bootstrap = createTypeBootstrapEngine project
        defineFlow bootstrap (readSeedTypeSource ()) |> ignore
        let policyResults = dispatch bootstrap "test-all" [] |> expectAllPassed "Campfire UTC and refresh policy seed"
        equal 10 policyResults.Count "seed policy tests execute before the host manifest is registered"
        dispatch bootstrap "commit" [ "word", jstr "utc-instant.valid?"; "library", jbool true ]
        |> expectOk "qualify the pure UTC validator before publishing its refined type"
        |> ignore
        dispatch bootstrap "commit" [ "word", jstr "UtcInstant" ]
        |> expectOk "publish the validated UTC instant type into the no-host project"
        |> ignore
        dispatch bootstrap "commit" [ "word", jstr "campfire.sessions.refresh-due?"; "library", jbool true ]
        |> expectOk "qualify the pure session refresh policy against the published UTC type"
        |> ignore
        dispatch bootstrap "commit" []
        |> expectOk "seed shared Campfire and authentication types before host registration"
        |> ignore

    let private effectCount (result: JsonNode) effectName =
        let actual = (result["effectAssertion"]["actual"]).AsObject()
        let mutable value = Unchecked.defaultof<JsonNode>
        if actual.TryGetPropertyValue(effectName, &value) then value.GetValue<int>() else 0

    let private assertEffectCounts (result: JsonNode) reads writes clocks =
        let caseName = result["name"].GetValue<string>()
        check ((result["effectAssertion"]["passed"]).GetValue<bool>()) $"{caseName} effect assertion passes"
        equal reads (effectCount result "db.read") $"{caseName} database read count"
        equal writes (effectCount result "db.write") $"{caseName} database write count"
        equal clocks (effectCount result "clock.read") $"{caseName} clock read count"

    let private fileHash path =
        use stream = File.OpenRead(path)
        SHA256.HashData(stream) |> Convert.ToHexString |> fun hash -> hash.ToLowerInvariant()

    let private loadTrustedCookieToken () =
        let vectorPath = Path.Combine(repositoryRoot, "tests", "AgentLang.Campfire.Cookies.Tests", "Fixtures", "rails-cookie-vectors.json")
        use document = JsonDocument.Parse(File.ReadAllText(vectorPath))
        let root = document.RootElement
        let secret = root.GetProperty("secret_key_base").GetString()
        let verifyVectors = root.GetProperty("signed_cookies").GetProperty("verify").EnumerateArray() |> Seq.toList
        let valid = verifyVectors |> List.find (fun value -> value.GetProperty("case").GetString().StartsWith("valid session_token", StringComparison.Ordinal))
        let validNow = DateTimeOffset.Parse(valid.GetProperty("now").GetString(), CultureInfo.InvariantCulture)
        let validRaw = valid.GetProperty("raw").GetString()
        let expectedToken = valid.GetProperty("expected").GetString()
        let service =
            match CookieService.Create(secret) with
            | Ok value -> value
            | Error failure -> failwith $"create CookieService for synthetic Rails vector: {failure}"
        try
            let token = service.VerifySessionToken(validRaw, validNow)
            equal (Some expectedToken) token "trusted Rails session-token vector verifies"
            let rejected =
                [ "tampered digest"
                  "permanent after 20 years"
                  "purpose of another cookie" ]
                |> List.map (fun caseName ->
                    let vector = verifyVectors |> List.find (fun value -> value.GetProperty("case").GetString() = caseName)
                    let now = DateTimeOffset.Parse(vector.GetProperty("now").GetString(), CultureInfo.InvariantCulture)
                    let raw = vector.GetProperty("raw").GetString()
                    equal None (service.VerifySessionToken(raw, now)) $"untrusted cookie case is rejected: {caseName}"
                    caseName)
            expectedToken, rejected
        finally
            (service :> IDisposable).Dispose()

    let private execute (connection: SqliteConnection) sql (parameters: (string * obj) list) =
        use command = connection.CreateCommand()
        command.CommandText <- sql
        for name, value in parameters do command.Parameters.AddWithValue(name, value) |> ignore
        command.ExecuteNonQuery()

    let private openConnection path mode =
        let builder = SqliteConnectionStringBuilder()
        builder.DataSource <- path
        builder.Mode <- mode
        builder.ForeignKeys <- true
        builder.Pooling <- false
        let connection = new SqliteConnection(builder.ToString())
        connection.Open()
        connection

    let private seedFixture path verifiedToken =
        use connection = openConnection path SqliteOpenMode.ReadWriteCreate
        use command = connection.CreateCommand()
        command.CommandText <-
            "CREATE TABLE users (id INTEGER PRIMARY KEY, name TEXT NOT NULL, role INTEGER NOT NULL, status INTEGER NOT NULL, bot_token TEXT, created_at TEXT NOT NULL, updated_at TEXT NOT NULL); " +
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
            "CREATE UNIQUE INDEX memberships_room_user_unique ON memberships(room_id, user_id); " +
            "CREATE TRIGGER reject_blocked_refresh BEFORE UPDATE ON sessions WHEN OLD.token = 'blocked-refresh-token' BEGIN SELECT RAISE(ABORT, 'test-only refresh failure'); END; " +
            "CREATE TRIGGER reject_session_delete BEFORE DELETE ON sessions WHEN OLD.id = 15 BEGIN SELECT RAISE(ABORT, 'test-only session delete failure'); END; " +
            "CREATE TRIGGER reject_push_subscription_delete BEFORE DELETE ON push_subscriptions WHEN OLD.endpoint = 'https://push.example.test/blocked' BEGIN SELECT RAISE(ABORT, 'test-only push delete failure'); END"
        command.ExecuteNonQuery() |> ignore

        for id in 1..9 do
            let role = if id <= 3 then 0 else if id <= 6 then 1 else 2
            let status = if id = 1 then 1 else if id % 3 = 1 then 0 else if id % 3 = 2 then 1 else 2
            let name = if id = 1 then "deactivated-member" else $"user-{id}"
            let botToken = if id = 7 then box "bot" else box DBNull.Value
            execute connection
                "INSERT INTO users(id, name, role, status, bot_token, created_at, updated_at) VALUES ($id, $name, $role, $status, $token, '2025-01-01 00:00:00', '2025-01-01 00:00:00')"
                [ "$id", box id; "$name", box name; "$role", box role; "$status", box status; "$token", botToken ]
            |> ignore

        let insertSession id userId token ip userAgent lastActive created updated =
            execute connection
                "INSERT INTO sessions(id, user_id, token, ip_address, user_agent, last_active_at, created_at, updated_at) VALUES ($id, $user, $token, $ip, $agent, $last, $created, $updated)"
                [ "$id", box id; "$user", box userId; "$token", box token
                  "$ip", (match ip with Some value -> box value | None -> box DBNull.Value)
                  "$agent", (match userAgent with Some value -> box value | None -> box DBNull.Value)
                  "$last", box lastActive; "$created", box created; "$updated", box updated ]
            |> ignore

        insertSession 10 1 verifiedToken (Some "198.51.100.8") (Some "stored-agent") "2026-01-01 11:00:00" "2025-01-01 00:00:00" "2025-01-01 00:00:00"
        insertSession 11 1 "due-token" (Some "198.51.100.8") (Some "stored-agent") "2025-12-31 00:00:00" "2025-01-01 00:00:00" "2025-12-31 00:00:00"
        insertSession 12 1 "blocked-refresh-token" (Some "198.51.100.8") (Some "stored-agent") "2025-12-31 00:00:00" "2025-01-01 00:00:00" "2025-12-31 00:00:00"
        insertSession 13 2 "pre-epoch-token" None None "1969-12-31 22:59:59.999998" "1969-12-31 23:59:59.999999" "1969-12-31 23:59:59.999999"
        execute connection "PRAGMA foreign_keys = OFF" [] |> ignore
        insertSession 14 99 "orphan-due-token" (Some "198.51.100.99") (Some "orphan-stored-agent") "2025-12-31 00:00:00" "2025-01-01 00:00:00" "2025-12-31 00:00:00"
        execute connection "PRAGMA foreign_keys = ON" [] |> ignore
        insertSession 15 1 "user-error-after-refresh-token" (Some "198.51.100.15") (Some "pre-refresh-agent") "2025-12-31 00:00:00" "2025-01-01 00:00:00" "2025-12-31 00:00:00"
        let insertPushSubscription id userId endpoint =
            execute connection
                "INSERT INTO push_subscriptions(id, user_id, endpoint, created_at, updated_at) VALUES ($id, $user, $endpoint, '2025-01-01 00:00:00', '2025-01-01 00:00:00')"
                [ "$id", box id; "$user", box userId; "$endpoint", box endpoint ]
            |> ignore
        insertPushSubscription 1 1 "https://push.example.test/current"
        insertPushSubscription 2 1 "https://push.example.test/current"
        insertPushSubscription 3 2 "https://push.example.test/current"
        insertPushSubscription 4 1 "https://push.example.test/other"
        insertPushSubscription 5 1 "https://push.example.test/blocked"
        insertPushSubscription 6 1 "https://push.example.test/blocked"
        execute connection
            "INSERT INTO bans(id, user_id, ip_address, created_at, updated_at) VALUES (1, 1, '203.0.113.99', '2025-01-01 00:00:00', '2025-01-01 00:00:00')"
            []
        |> ignore
        execute connection
            "INSERT INTO rooms(id, created_at, creator_id, name, type, updated_at) VALUES (1, '2025-01-01 00:00:00', 1, 'Main', 'Rooms::Open', '2025-01-01 00:00:00')"
            []
        |> ignore
        execute connection
            "INSERT INTO memberships(id, room_id, user_id, created_at, updated_at) VALUES (1, 1, 1, '2025-01-01 00:00:00', '2025-01-01 00:00:00')"
            []
        |> ignore

    let private scalarString path sql parameters =
        use connection = openConnection path SqliteOpenMode.ReadWrite
        use command = connection.CreateCommand()
        command.CommandText <- sql
        for name, value in parameters do command.Parameters.AddWithValue(name, value) |> ignore
        Convert.ToString(command.ExecuteScalar(), CultureInfo.InvariantCulture)

    let private scalarLong path sql parameters =
        use connection = openConnection path SqliteOpenMode.ReadWrite
        use command = connection.CreateCommand()
        command.CommandText <- sql
        for name, value in parameters do command.Parameters.AddWithValue(name, value) |> ignore
        Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture)

    let private sentinelProvider (calls: int ref) =
        { OperationIds = CampfireAuthenticationHost.operationIds
          Invoke = fun _ _ -> calls.Value <- calls.Value + 1; Error HostOperationFailure.InvocationFailed
          Dispose = ignore }

    let private createProbeFactory verifiedToken =
        let probes = ResizeArray<ProviderProbe>()
        let mutable calls = 0
        let factory () =
            calls <- calls + 1
            Directory.CreateDirectory(runtimeDirectory) |> ignore
            let path = Path.Combine(runtimeDirectory, $"flow-test-provider-{calls:D2}-{Guid.NewGuid():N}.db")
            seedFixture path verifiedToken
            match CampfireAuthenticationHost.createProvider path with
            | Error _ -> Error HostOperationFailure.InvocationFailed
            | Ok provider ->
                let probe = ProviderProbe(path, provider)
                probes.Add probe
                Ok probe.AsProvider
        probes, (fun () -> calls), factory

    let private resultByName (results: JsonArray) caseName =
        results |> Seq.find (fun result -> result["name"].GetValue<string>() = caseName)

    let private probeToken (probe: ProviderProbe) =
        match probe.Calls |> List.tryHead with
        | Some("campfire.sessions.find-by-token", [ NamedValue("SessionToken", StringValue token) ]) -> token
        | _ -> ""

    let private operationNames (probe: ProviderProbe) = probe.Calls |> List.map fst

    let private checkScopedAndRealExecution verifiedToken =
        let probes, factoryCalls, createTestProvider = createProbeFactory verifiedToken
        let productionCalls = ref 0
        let environment = CampfireAuthenticationHost.createEnvironment (sentinelProvider productionCalls) createTestProvider
        let project = projectDirectory
        Directory.CreateDirectory(project) |> ignore
        seedTypesWithoutHost project

        let app = readApplicationSource verifiedToken true
        let engine = createEngine project environment
        defineFlow engine app |> ignore
        let results = dispatch engine "test-all" [] |> expectAllPassed "Campfire auth scoped and real SQLite suites"
        equal 43 results.Count "suite includes ten session-policy, 27 scoped-restoration, and six real SQLite cases"
        equal 6 (factoryCalls ()) "real attached tests create one isolated provider per case"
        equal 0 productionCalls.Value "tests never invoke the production provider"

        let scoped = results |> Seq.filter (fun result -> result["word"].GetValue<string>() = "campfire.authentication.restore-session" && result["name"].GetValue<string>() <> "restores-cookie-verified-token-without-status-filter" && result["name"].GetValue<string>() <> "refreshes-due-session-and-requests-cookie-update" && result["name"].GetValue<string>() <> "orphaned-user-session-still-refreshes" && result["name"].GetValue<string>() <> "user-lookup-error-after-refresh-propagates" && result["name"].GetValue<string>() <> "missing-token-is-ordinary-absence" && result["name"].GetValue<string>() <> "refresh-storage-error-propagates") |> Seq.toList
        equal 27 scoped.Length "all restoration policy cases run under scoped provider substitutions"
        for result in scoped do assertEffectCounts result 0 0 0
        let statusCases = scoped |> List.filter (fun result -> result["name"].GetValue<string>().EndsWith("-is-restored", StringComparison.Ordinal))
        equal 9 statusCases.Length "all nine role/status combinations remain covered without filtering"

        let realExpected =
            [ "restores-cookie-verified-token-without-status-filter", 2, 0, 0
              "refreshes-due-session-and-requests-cookie-update", 2, 1, 0
              "orphaned-user-session-still-refreshes", 2, 1, 0
              "user-lookup-error-after-refresh-propagates", 2, 1, 0
              "missing-token-is-ordinary-absence", 1, 0, 0
              "refresh-storage-error-propagates", 1, 1, 0 ]
        for name, reads, writes, clocks in realExpected do
            resultByName results name |> fun result -> assertEffectCounts result reads writes clocks

        let allProbes = probes |> Seq.toList
        equal 6 allProbes.Length "one probe retained per real attached test"
        equal 6 (allProbes |> List.map (fun probe -> probe.Path) |> Set.ofList |> Set.count) "each attached test has a unique SQLite fixture"
        for probe in allProbes do equal 1 probe.DisposeCalls $"test provider disposed once for {Path.GetFileName(probe.Path)}"

        let expectedCalls token =
            match token with
            | value when value = verifiedToken -> [ "campfire.sessions.find-by-token"; "campfire.users.find-by-id" ]
            | "not-in-database" -> [ "campfire.sessions.find-by-token" ]
            | "due-token"
            | "orphan-due-token"
            | "user-error-after-refresh-token" -> [ "campfire.sessions.find-by-token"; "campfire.sessions.refresh-before"; "campfire.users.find-by-id" ]
            | "blocked-refresh-token" -> [ "campfire.sessions.find-by-token"; "campfire.sessions.refresh-before" ]
            | other -> failwith $"unexpected real test token in provider probe: {other}"
        for probe in allProbes do
            equal (expectedCalls (probeToken probe)) (operationNames probe) $"{probeToken probe} follows the Flow-selected operation order"

        let dueProbe = allProbes |> List.find (fun probe -> probeToken probe = "due-token")
        equal "203.0.113.8" (scalarString dueProbe.Path "SELECT ip_address FROM sessions WHERE id = 11" []) "due refresh persists request IP"
        equal "refreshed-agent" (scalarString dueProbe.Path "SELECT user_agent FROM sessions WHERE id = 11" []) "due refresh persists request user-agent"
        equal "2026-01-01 12:00:00" (scalarString dueProbe.Path "SELECT last_active_at FROM sessions WHERE id = 11" []) "due refresh persists supplied UTC time"
        equal "2026-01-01 12:00:00" (scalarString dueProbe.Path "SELECT updated_at FROM sessions WHERE id = 11" []) "due refresh updates the timestamp"

        let orphanProbe = allProbes |> List.find (fun probe -> probeToken probe = "orphan-due-token")
        equal 0L (scalarLong orphanProbe.Path "SELECT COUNT(*) FROM users WHERE id = 99" []) "orphan fixture has no user row after the SQLite connection is reopened"
        equal "203.0.113.9" (scalarString orphanProbe.Path "SELECT ip_address FROM sessions WHERE id = 14" []) "orphan session refresh persists request IP"
        equal "orphan-agent" (scalarString orphanProbe.Path "SELECT user_agent FROM sessions WHERE id = 14" []) "orphan session refresh persists request user-agent"
        equal "2026-01-01 12:00:00" (scalarString orphanProbe.Path "SELECT last_active_at FROM sessions WHERE id = 14" []) "orphan session refresh persists supplied UTC time after the test connection closes"
        equal "2026-01-01 12:00:00" (scalarString orphanProbe.Path "SELECT updated_at FROM sessions WHERE id = 14" []) "orphan session updated_at persists after reopening SQLite"

        let userErrorProbe = allProbes |> List.find (fun probe -> probeToken probe = "user-error-after-refresh-token")
        equal "203.0.113.15" (scalarString userErrorProbe.Path "SELECT ip_address FROM sessions WHERE id = 15" []) "session refresh remains committed when the following user lookup returns an error"
        equal "error-agent" (scalarString userErrorProbe.Path "SELECT user_agent FROM sessions WHERE id = 15" []) "request metadata remains committed before the user lookup error"
        equal "2026-01-01 12:00:00" (scalarString userErrorProbe.Path "SELECT last_active_at FROM sessions WHERE id = 15" []) "refreshed session remains persisted after user lookup error and connection reopen"

        let freshProbe = allProbes |> List.find (fun probe -> probeToken probe = verifiedToken)
        equal "198.51.100.8" (scalarString freshProbe.Path "SELECT ip_address FROM sessions WHERE id = 10" []) "fresh session retains stored IP"
        equal "stored-agent" (scalarString freshProbe.Path "SELECT user_agent FROM sessions WHERE id = 10" []) "fresh session retains stored user-agent"
        equal "2026-01-01 11:00:00" (scalarString freshProbe.Path "SELECT last_active_at FROM sessions WHERE id = 10" []) "exactly-hour session is not refreshed"

        let blockedProbe = allProbes |> List.find (fun probe -> probeToken probe = "blocked-refresh-token")
        equal "2025-12-31 00:00:00" (scalarString blockedProbe.Path "SELECT last_active_at FROM sessions WHERE id = 12" []) "failed refresh trigger leaves session unchanged"
        equal "2025-12-31 00:00:00" (scalarString blockedProbe.Path "SELECT updated_at FROM sessions WHERE id = 12" []) "failed refresh leaves updated_at unchanged"

        let beforeLibraryQualificationProbeCount = probes.Count
        dispatch engine "commit" [ "word", jstr "campfire.authentication.restore-session"; "library", jbool true ]
        |> expectOk "qualify the session-restoration word as a library"
        |> ignore
        let qualificationProbes = probes |> Seq.skip beforeLibraryQualificationProbeCount |> Seq.toList
        equal 6 qualificationProbes.Length "library qualification reruns the six real attached restoration cases"
        for probe in qualificationProbes do equal 1 probe.DisposeCalls $"qualification test provider disposed once for {Path.GetFileName(probe.Path)}"
        equal 0 productionCalls.Value "library qualification does not call production provider"
        let reloaded = createEngine project environment
        let reloadValidationProbes = probes |> Seq.skip (beforeLibraryQualificationProbeCount + qualificationProbes.Length) |> Seq.toList
        equal 6 reloadValidationProbes.Length "fresh engine library validation creates isolated providers for real cases"
        for probe in reloadValidationProbes do equal 1 probe.DisposeCalls $"reload validation provider disposed once for {Path.GetFileName(probe.Path)}"
        let reloadedWords = dispatch reloaded "words" [] |> expectOk "inspect auth library after fresh engine reload"
        let wordNames = (reloadedWords["data"]["words"]).AsArray() |> Seq.map (fun item -> item["name"].GetValue<string>()) |> Set.ofSeq
        let libraryWords =
            [ "utc-instant.valid?"; "campfire.sessions.refresh-due?"; "campfire.authentication.restore-session" ]
        for word in libraryWords do check (wordNames.Contains word) $"fresh manifest-backed engine reloads {word} as a library"
        for word in libraryWords do
            let description = dispatch reloaded "describe" [ "word", jstr word ] |> expectOk $"describe reloaded library word {word}"
            equal "library" ((description["data"]["maturity"]).GetValue<string>()) $"{word} keeps library maturity after reload"

        let beforeReloadFactoryCount = factoryCalls ()
        let beforeReloadProbeCount = probes.Count
        let reloadResults = dispatch reloaded "test-all" [] |> expectAllPassed "reloaded Campfire auth suite"
        equal 43 reloadResults.Count "fresh engine retains all policy and integration cases"
        equal (beforeReloadFactoryCount + 6) (factoryCalls ()) "reloaded real cases each construct a fresh provider"
        equal 0 productionCalls.Value "fresh reloaded suite does not call production provider"
        let reloadScopes =
            reloadResults
            |> Seq.filter (fun result ->
                result["word"].GetValue<string>() = "campfire.authentication.restore-session"
                && result["name"].GetValue<string>() <> "restores-cookie-verified-token-without-status-filter"
                && result["name"].GetValue<string>() <> "refreshes-due-session-and-requests-cookie-update"
                && result["name"].GetValue<string>() <> "orphaned-user-session-still-refreshes"
                && result["name"].GetValue<string>() <> "user-lookup-error-after-refresh-propagates"
                && result["name"].GetValue<string>() <> "missing-token-is-ordinary-absence"
                && result["name"].GetValue<string>() <> "refresh-storage-error-propagates")
            |> Seq.toList
        equal 27 reloadScopes.Length "all scoped restoration cases survive library reload"
        for result in reloadScopes do assertEffectCounts result 0 0 0
        let reloadProbes = probes |> Seq.skip beforeReloadProbeCount |> Seq.toList
        equal 6 reloadProbes.Length "fresh engine creates one new provider per real attached test"
        equal 24 (probes |> Seq.map (fun probe -> probe.Path) |> Set.ofSeq |> Set.count) "all test, qualification, and reload provider fixtures are isolated"
        for probe in probes do
            equal 1 probe.DisposeCalls $"every test provider is disposed once for {Path.GetFileName(probe.Path)}"
            equal (expectedCalls (probeToken probe)) (operationNames probe) $"{probeToken probe} keeps the expected call order across tests and reloads"
        results.Count

    let private ensureTypedResult expectedType = function
        | ResultValue(okType, TNamed "DbError", Ok value) when okType = expectedType -> value
        | other -> failwith $"unexpected host result value: {other}"

    let private adapterContract verifiedToken =
        equal 11 CampfireAuthenticationHost.manifest.Count "manifest publishes exactly eleven closed authentication operations"
        equal CampfireAuthenticationHost.operationIds (CampfireAuthenticationHost.manifest |> Map.toSeq |> Seq.map fst |> Set.ofSeq) "manifest and operation IDs are identical"
        for KeyValue(operationId, definition) in CampfireAuthenticationHost.manifest do
            equal operationId definition.Id $"manifest entry {definition.WordName} carries its own ID"
            equal (match operationId with HostOperationId name -> name) definition.WordName $"operation ID matches its exact Flow name"

        let path = Path.Combine(runtimeDirectory, $"adapter-contract-{Guid.NewGuid():N}.db")
        seedFixture path verifiedToken
        let provider =
            match CampfireAuthenticationHost.createProvider path with
            | Ok value -> value
            | Error error -> failwith $"open adapter contract fixture: {error}"
        let id name = HostOperationId name

        let malformed = provider.Invoke (id "campfire.sessions.find-by-token") [ StringValue "wrong metadata" ]
        equal (Error HostOperationFailure.ProviderContractMismatch) malformed "session adapter rejects a malformed nominal input"
        equal (Error HostOperationFailure.ProviderContractMismatch)
            (provider.Invoke (id "campfire.sessions.destroy") [ IntValue 11L ])
            "session destroy rejects an unwrapped session ID"
        equal (Error HostOperationFailure.ProviderContractMismatch)
            (provider.Invoke (id "campfire.push-subscriptions.destroy-for-user") [ IntValue 1L; StringValue "https://push.example.test/current" ])
            "push destroy rejects an unwrapped user ID"
        equal (Error HostOperationFailure.MissingHandler) (provider.Invoke (id "campfire.raw-sql") []) "adapter rejects an unknown operation"

        let expectUnitSuccess label result =
            match result with
            | Ok [ ResultValue(TUnit, TNamed "DbError", Ok UnitValue) ] -> check true label
            | other -> failwith $"{label}: expected typed Unit success, got {other}"

        let expectUnavailable label result =
            match result with
            | Ok [ ResultValue(TUnit, TNamed "DbError", Error(EnumValue("DbError", "unavailable", None))) ] -> check true label
            | other -> failwith $"{label}: expected structured DbError.Unavailable, got {other}"

        let sessionValue =
            provider.Invoke (id "campfire.sessions.find-by-token") [ NamedValue("SessionToken", StringValue verifiedToken) ]
            |> function
                | Ok [ value ] -> ensureTypedResult (TOption(TNamed "SessionRow")) value
                | other -> failwith $"session lookup contract failed: {other}"
        match sessionValue with
        | OptionValue(TNamed "SessionRow", Some(RecordValue("SessionRow", fields))) ->
            equal (Some(NamedValue("SessionToken", StringValue verifiedToken))) (fields.TryFind "token") "session token survives exact value conversion"
            equal (Some(OptionValue(TNamed "RemoteIp", Some(NamedValue("RemoteIp", StringValue "198.51.100.8"))))) (fields.TryFind "ip-address") "optional remote IP retains its nominal wrapper"
            equal (Some(NamedValue("UtcInstant", IntValue 1767265200000000L))) (fields.TryFind "last-active-at") "session timestamp maps to UTC microseconds"
            equal (Some(NamedValue("UtcInstant", IntValue 1735689600000000L))) (fields.TryFind "created-at") "session creation time maps to Unix microseconds"
        | other -> failwith $"session lookup returned an unexpected Option payload: {other}"

        let preEpochValue =
            provider.Invoke (id "campfire.sessions.find-by-token") [ NamedValue("SessionToken", StringValue "pre-epoch-token") ]
            |> function
                | Ok [ value ] -> ensureTypedResult (TOption(TNamed "SessionRow")) value
                | other -> failwith $"pre-epoch session lookup contract failed: {other}"
        match preEpochValue with
        | OptionValue(TNamed "SessionRow", Some(RecordValue("SessionRow", fields))) ->
            equal (Some(NamedValue("UtcInstant", IntValue -1L))) (fields.TryFind "created-at") "pre-epoch timestamp maps to negative fractional microseconds"
        | other -> failwith $"pre-epoch lookup returned an unexpected Option payload: {other}"

        let refreshInput =
            RecordValue("SessionRefresh", Map.ofList
                [ "session-id", NamedValue("SessionId", IntValue 11L)
                  "cutoff", NamedValue("UtcInstant", IntValue 1767265200000000L)
                  "now", NamedValue("UtcInstant", IntValue 1767268800000000L)
                  "ip-address", OptionValue(TNamed "RemoteIp", Some(NamedValue("RemoteIp", StringValue "203.0.113.8")))
                  "user-agent", OptionValue(TString, Some(StringValue "adapter-agent")) ])
        let refreshedValue =
            provider.Invoke (id "campfire.sessions.refresh-before") [ refreshInput ]
            |> function
                | Ok [ value ] -> ensureTypedResult (TNamed "SessionRow") value
                | other -> failwith $"session refresh contract failed: {other}"
        match refreshedValue with
        | RecordValue("SessionRow", fields) ->
            equal (Some(NamedValue("UtcInstant", IntValue 1767268800000000L))) (fields.TryFind "last-active-at") "refresh output carries explicit timestamp"
            equal (Some(OptionValue(TNamed "RemoteIp", Some(NamedValue("RemoteIp", StringValue "203.0.113.8"))))) (fields.TryFind "ip-address") "refresh preserves supplied optional IP"
        | other -> failwith $"session refresh returned an unexpected record: {other}"

        let userValue =
            provider.Invoke (id "campfire.users.find-by-id") [ NamedValue("UserId", IntValue 1L) ]
            |> function
                | Ok [ value ] -> ensureTypedResult (TOption(TNamed "AuthUser")) value
                | other -> failwith $"user lookup contract failed: {other}"
        match userValue with
        | OptionValue(TNamed "AuthUser", Some(RecordValue("AuthUser", fields))) ->
            equal (Some(EnumValue("Role", "member", None))) (fields.TryFind "role") "user role maps into shared Role enum"
            equal (Some(EnumValue("AuthUserStatus", "deactivated", None))) (fields.TryFind "status") "user status maps into closed AuthUserStatus enum without a filter"
        | other -> failwith $"user lookup returned an unexpected Option payload: {other}"

        let botValue =
            provider.Invoke (id "campfire.users.authenticate-bot-key") [ NamedValue("BotKey", StringValue "7-bot") ]
            |> function
                | Ok [ value ] -> ensureTypedResult (TOption(TNamed "AuthUser")) value
                | other -> failwith $"bot lookup contract failed: {other}"
        match botValue with
        | OptionValue(TNamed "AuthUser", Some(RecordValue("AuthUser", fields))) ->
            equal (Some(EnumValue("Role", "bot", None))) (fields.TryFind "role") "active bot lookup preserves bot role"
            equal (Some(EnumValue("AuthUserStatus", "active", None))) (fields.TryFind "status") "active bot lookup preserves active status"
        | other -> failwith $"bot lookup returned an unexpected Option payload: {other}"

        match provider.Invoke (id "campfire.bans.ip-banned?") [ NamedValue("RemoteIp", StringValue "203.0.113.99") ] with
        | Ok [ ResultValue(TBool, TNamed "DbError", Ok(BoolValue true)) ] -> check true "IP ban lookup returns typed true"
        | other -> failwith $"ban lookup contract failed: {other}"

        let roomValue =
            provider.Invoke (id "campfire.rooms.find-for-user") [ NamedValue("UserId", IntValue 1L); NamedValue("RoomId", IntValue 1L) ]
            |> function
                | Ok [ value ] -> ensureTypedResult (TOption(TNamed "RoomRow")) value
                | other -> failwith $"room lookup contract failed: {other}"
        match roomValue with
        | OptionValue(TNamed "RoomRow", Some(RecordValue("RoomRow", fields))) ->
            equal (Some(EnumValue("RoomKind", "open", None))) (fields.TryFind "kind") "room lookup preserves closed room kind"
        | other -> failwith $"room lookup returned an unexpected Option payload: {other}"

        let destroySession = id "campfire.sessions.destroy"
        let sessionArguments = [ NamedValue("SessionId", IntValue 11L) ]
        expectUnitSuccess "session destroy returns a typed Unit result" (provider.Invoke destroySession sessionArguments)
        expectUnitSuccess "session destroy treats a missing row as success" (provider.Invoke destroySession sessionArguments)
        equal 0L (scalarLong path "SELECT COUNT(*) FROM sessions WHERE id = $id" [ "$id", box 11L ]) "host session destroy removes exactly its target row"
        equal 1L (scalarLong path "SELECT COUNT(*) FROM sessions WHERE id = $id" [ "$id", box 10L ]) "host session destroy leaves another session intact"
        expectUnavailable "session destroy maps SQLite trigger failure to DbError.Unavailable"
            (provider.Invoke destroySession [ NamedValue("SessionId", IntValue 15L) ])
        equal 1L (scalarLong path "SELECT COUNT(*) FROM sessions WHERE id = $id" [ "$id", box 15L ]) "failed host session destroy preserves its row"

        let destroyPush = id "campfire.push-subscriptions.destroy-for-user"
        let currentEndpoint = "https://push.example.test/current"
        let pushArguments = [ NamedValue("UserId", IntValue 1L); StringValue currentEndpoint ]
        expectUnitSuccess "push destroy returns a typed Unit result" (provider.Invoke destroyPush pushArguments)
        expectUnitSuccess "push destroy treats missing matches as success" (provider.Invoke destroyPush pushArguments)
        equal 0L (scalarLong path "SELECT COUNT(*) FROM push_subscriptions WHERE user_id = $user AND endpoint = $endpoint" [ "$user", box 1L; "$endpoint", box currentEndpoint ]) "host push destroy removes every duplicate match for that user"
        equal 1L (scalarLong path "SELECT COUNT(*) FROM push_subscriptions WHERE user_id = $user AND endpoint = $endpoint" [ "$user", box 2L; "$endpoint", box currentEndpoint ]) "host push destroy leaves another user's matching endpoint"
        equal 1L (scalarLong path "SELECT COUNT(*) FROM push_subscriptions WHERE user_id = $user AND endpoint = $endpoint" [ "$user", box 1L; "$endpoint", box "https://push.example.test/other" ]) "host push destroy leaves the user's other endpoint"
        expectUnavailable "push destroy maps SQLite trigger failure to DbError.Unavailable"
            (provider.Invoke destroyPush [ NamedValue("UserId", IntValue 1L); StringValue "https://push.example.test/blocked" ])
        equal 2L (scalarLong path "SELECT COUNT(*) FROM push_subscriptions WHERE user_id = $user AND endpoint = $endpoint" [ "$user", box 1L; "$endpoint", box "https://push.example.test/blocked" ]) "failed multi-row host push destroy preserves every matching row"
        provider.Dispose()

        // Negative Unix microseconds exercise the reverse conversion as well as the DB timestamp parser.
        let negativeRefreshPath = Path.Combine(runtimeDirectory, $"pre-epoch-{Guid.NewGuid():N}.db")
        seedFixture negativeRefreshPath verifiedToken
        let negativeProvider =
            match CampfireAuthenticationHost.createProvider negativeRefreshPath with
            | Ok value -> value
            | Error error -> failwith $"open negative timestamp fixture: {error}"
        let negativeRequest =
            RecordValue("SessionRefresh", Map.ofList
                [ "session-id", NamedValue("SessionId", IntValue 13L)
                  "cutoff", NamedValue("UtcInstant", IntValue -3600000001L)
                  "now", NamedValue("UtcInstant", IntValue -1L)
                  "ip-address", OptionValue(TNamed "RemoteIp", None)
                  "user-agent", OptionValue(TString, None) ])
        match negativeProvider.Invoke (id "campfire.sessions.refresh-before") [ negativeRequest ] with
        | Ok [ ResultValue(TNamed "SessionRow", TNamed "DbError", Ok _) ] -> check true "pre-epoch conditional refresh returns typed success"
        | other -> failwith $"pre-epoch refresh failed: {other}"
        equal "1969-12-31 23:59:59.999999" (scalarString negativeRefreshPath "SELECT last_active_at FROM sessions WHERE id = 13" []) "negative microsecond time round trips without truncating toward zero"
        equal "1969-12-31 23:59:59.999999" (scalarString negativeRefreshPath "SELECT updated_at FROM sessions WHERE id = 13" []) "negative microsecond updated_at round trips"
        negativeProvider.Dispose()

    let private writePrivateRunSummary (verifiedToken: string) (rejectedCases: string list) (passedCount: int) =
        let tokenDigest = SHA256.HashData(Encoding.UTF8.GetBytes(verifiedToken)) |> Convert.ToHexString |> fun value -> value.ToLowerInvariant()
        let report = JsonObject()
        report["status"] <- JsonValue.Create("passed")
        report["assertions"] <- JsonValue.Create(assertions)
        report["attached_cases"] <- JsonValue.Create(passedCount)
        report["verified_cookie_token_sha256"] <- JsonValue.Create(tokenDigest)
        report["rejected_cookie_cases"] <- JsonSerializer.SerializeToNode(rejectedCases)
        report["private_run_directory"] <- JsonValue.Create(runDirectory)
        File.WriteAllText(Path.Combine(runDirectory, "run-receipt.json"), report.ToJsonString() + Environment.NewLine, UTF8Encoding(false))

    let private run () =
        Directory.CreateDirectory(runtimeDirectory) |> ignore
        Directory.CreateDirectory(projectDirectory) |> ignore
        Environment.SetEnvironmentVariable("TEMP", runtimeDirectory)
        Environment.SetEnvironmentVariable("TMP", runtimeDirectory)

        let verifiedToken, rejectedCookieCases = loadTrustedCookieToken ()
        check (not (String.IsNullOrWhiteSpace verifiedToken)) "cookie boundary yields a nonempty verified session token"
        equal 3 rejectedCookieCases.Length "tampered, expired, and wrong-purpose cookies stop at the cookie boundary"
        let sourcePaths =
            [ "applications/campfire/authentication-types.flow"
              "applications/campfire/authentication.flow"
              "applications/campfire/authentication-tests.flow"
              "applications/campfire/Authentication.Host/CampfireAuthenticationHost.fs"
              "tests/AgentLang.Campfire.Authentication.Integration.Tests/Program.fs"
              "tests/AgentLang.Campfire.Authentication.Integration.Tests/real-authentication-tests.flow" ]
        let hashes = sourcePaths |> List.map (fun relative -> relative, fileHash (Path.Combine(repositoryRoot, relative)))
        let sourceManifest = JsonObject()
        for path, hash in hashes do sourceManifest[path] <- JsonValue.Create(hash)
        File.WriteAllText(Path.Combine(runDirectory, "source-hashes.json"), sourceManifest.ToJsonString() + Environment.NewLine, UTF8Encoding(false))

        adapterContract verifiedToken
        let passedCount = checkScopedAndRealExecution verifiedToken
        writePrivateRunSummary verifiedToken rejectedCookieCases passedCount
        printfn "Passed %d assertions across %d attached Flow cases; private evidence: %s" assertions passedCount runDirectory

    [<EntryPoint>]
    let main _ =
        try
            run ()
            0
        with error ->
            Directory.CreateDirectory(runDirectory) |> ignore
            File.WriteAllText(Path.Combine(runDirectory, "failure.txt"), error.ToString() + Environment.NewLine, UTF8Encoding(false))
            eprintfn "Campfire authentication integration failed. Private diagnostics: %s" (Path.Combine(runDirectory, "failure.txt"))
            eprintfn "%s" error.Message
            1
