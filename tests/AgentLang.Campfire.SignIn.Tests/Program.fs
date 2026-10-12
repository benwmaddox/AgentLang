module AgentLang.Campfire.SignIn.Tests

open System
open System.Globalization
open System.IO
open System.Security.Cryptography
open System.Text.Json.Nodes
open AgentLang
open AgentLang.Campfire.Authentication.Host
open Microsoft.Data.Sqlite

let mutable private assertions = 0
let private check condition message =
    assertions <- assertions + 1
    if not condition then failwith message

let private root = Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, "../.."))
let private proof = Path.Combine(root, ".agentlang/campfire-login-243/sign-in", DateTime.UtcNow.ToString("yyyyMMddTHHmmssfffZ") + "-" + Guid.NewGuid().ToString("N"))
let private capabilities = Set.ofList [ "db.read"; "db.write"; "random.read" ]
let private read path = File.ReadAllText(Path.Combine(root, path))
let private hash path = SHA256.HashData(File.ReadAllBytes(path)) |> Convert.ToHexString |> fun value -> value.ToLowerInvariant()
let private sourcePaths =
    [ "applications/campfire/campfire-types.flow"
      "applications/campfire/session-policy.flow"
      "applications/campfire/authentication-types.flow"
      "applications/campfire/sign-in.flow"
      "applications/campfire/sign-in-tests.flow"
      "applications/campfire/Authentication.Persistence/SqliteProvider.fs"
      "applications/campfire/Authentication.Persistence/PasswordVerification.fs"
      "applications/campfire/Authentication.Persistence/AgentLang.Campfire.Authentication.Persistence.fsproj"
      "applications/campfire/Authentication.Host/CampfireAuthenticationHost.fs"
      "tests/AgentLang.Campfire.SignIn.Tests/Program.fs" ]

let private dispatch (engine: Runtime.Engine) operation (fields: (string * JsonNode) list) =
    let args = JsonObject()
    for name, value in fields do args[name] <- value
    engine.Dispatch(operation, args)

let private ok label (response: JsonObject) =
    check ((response["ok"]).GetValue<bool>()) (label + ": " + response.ToJsonString())
    response

let private jstr (value: string) = JsonValue.Create(value) :> JsonNode
let private define engine source =
    dispatch engine "define" [ "frontend", jstr "flow"; "syntaxVersion", JsonValue.Create(2) :> JsonNode; "source", jstr source ]
    |> ok "define fixed application source" |> ignore

let private commit engine word (library: bool) =
    dispatch engine "commit" [ "word", jstr word; "library", JsonValue.Create(library) :> JsonNode ]
    |> ok ("commit " + word) |> ignore

let private seedTypes project =
    let engine = Runtime.Engine(project, capabilities, "2026-10-12T12:00:00Z", fileSystemMode = FileSystemMode.Virtual, testCapabilities = capabilities)
    [ "applications/campfire/campfire-types.flow"; "applications/campfire/session-policy.flow"; "applications/campfire/authentication-types.flow" ]
    |> List.map read |> String.concat "\n\n" |> define engine
    commit engine "utc-instant.valid?" true
    commit engine "UtcInstant" false
    commit engine "campfire.sessions.refresh-due?" true
    dispatch engine "commit" [] |> ok "commit shared types" |> ignore

let private createDatabase path =
    let builder = SqliteConnectionStringBuilder(DataSource = path, Mode = SqliteOpenMode.ReadWriteCreate, ForeignKeys = Nullable true, Pooling = false)
    use connection = new SqliteConnection(builder.ToString())
    connection.Open()
    use command = connection.CreateCommand()
    command.CommandText <-
        "CREATE TABLE users (id INTEGER PRIMARY KEY, name TEXT NOT NULL, role INTEGER NOT NULL, status INTEGER NOT NULL, email_address TEXT, password_digest TEXT, bot_token TEXT, created_at TEXT NOT NULL, updated_at TEXT NOT NULL); " +
        "CREATE TABLE sessions (id INTEGER PRIMARY KEY, user_id INTEGER NOT NULL REFERENCES users(id), token TEXT NOT NULL UNIQUE, ip_address TEXT, user_agent TEXT, last_active_at TEXT NOT NULL, created_at TEXT NOT NULL, updated_at TEXT NOT NULL); " +
        "INSERT INTO users VALUES (1, 'Synthetic Member', 0, 0, 'member@example.test', $digest, NULL, '2026-01-01 00:00:00', '2026-01-01 00:00:00');"
    // Independent Rails-produced vector, not a digest made by the tested package.
    command.Parameters.AddWithValue("$digest", "$2a$12$I/2qP3ixlNN..JU2dXSN0umG7/Folk3aUaqgSHn/Rl0G82O9WYk66") |> ignore
    command.ExecuteNonQuery() |> ignore

let private sessionCount path =
    use connection = new SqliteConnection(SqliteConnectionStringBuilder(DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false).ToString())
    connection.Open()
    use command = connection.CreateCommand()
    command.CommandText <- "SELECT count(*) FROM sessions"
    Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture)

let private entry (engine: Runtime.Engine) =
    engine.BindFlowEntryPoint("campfire.authentication.sign-in", [ TString; TString; TNamed "UtcInstant"; TOption(TNamed "RemoteIp"); TOption TString ], [ TResult(TOption(TNamed "SessionRow"), TNamed "DbError") ])

let private inputs email password =
    [ StringValue email
      StringValue password
      NamedValue("UtcInstant", IntValue 1791806400000000L)
      OptionValue(TNamed "RemoteIp", Some(NamedValue("RemoteIp", StringValue "192.0.2.34")))
      OptionValue(TString, Some(StringValue "Synthetic SignIn Test")) ]

let private assertNone (handle: Runtime.FlowEntryPoint) email password =
    match handle.Invoke(inputs email password) with
    | Ok result -> check (result.Values = [ ResultValue(TOption(TNamed "SessionRow"), TNamed "DbError", Ok(OptionValue(TNamed "SessionRow", None))) ]) "credential rejection returns ordinary typed absence"
    | Error _ -> failwith "credential rejection unexpectedly raised a runtime diagnostic"

let private run () =
    Directory.CreateDirectory(proof) |> ignore
    let project = Path.Combine(proof, "project")
    seedTypes project
    let database = Path.Combine(proof, "synthetic.db")
    createDatabase database
    let raw =
        match CampfireAuthenticationHost.createProvider database with
        | Ok provider -> provider
        | Error _ -> failwith "open synthetic database"
    let productionCalls = ResizeArray<string>()
    let provider = { raw with Invoke = fun id values -> let (HostOperationId name) = id in productionCalls.Add name; raw.Invoke id values }
    let mutable factories = 0
    let environment = CampfireAuthenticationHost.createEnvironment provider (fun () -> factories <- factories + 1; Error HostOperationFailure.TestProviderUnavailable)
    let makeEngine caps = Runtime.Engine(project, caps, "2026-10-12T12:00:00Z", fileSystemMode = FileSystemMode.Virtual, testCapabilities = capabilities, hostOperations = environment)
    let engine = makeEngine capabilities
    define engine (read "applications/campfire/sign-in.flow" + "\n\n" + read "applications/campfire/sign-in-tests.flow")
    let tests = dispatch engine "test" [ "word", jstr "campfire.authentication.sign-in" ] |> ok "run scoped sign-in tests"
    let results = (tests["data"]["results"]).AsArray()
    check (results.Count > 0) "sign-in has attached test evidence"
    for result in results do check ((result["passed"]).GetValue<bool>()) ("scoped sign-in case failed: " + (result["name"]).GetValue<string>())
    check (factories = 0 && productionCalls.Count = 0 && sessionCount database = 0L) "scoped tests never construct a provider or alter production SQLite"
    commit engine "campfire.authentication.sign-in" true
    check (factories = 0 && productionCalls.Count = 0) "library qualification uses only scoped replacements"
    let reloaded = makeEngine capabilities
    let description = dispatch reloaded "describe" [ "word", jstr "campfire.authentication.sign-in" ] |> ok "describe reloaded sign-in"
    check ((description["data"]["maturity"]).GetValue<string>() = "library") "sign-in retains library maturity after reload"
    let handle = entry reloaded
    let beforeDenied = productionCalls.Count
    let denied = entry (makeEngine (Set.singleton "db.read"))
    match denied.Invoke(inputs "member@example.test" "secret123456") with
    | Error diagnostic -> check (diagnostic.Code = "CAPABILITY_DENIED") "missing write/random capability is rejected"
    | Ok _ -> failwith "denied sign-in executed"
    check (productionCalls.Count = beforeDenied && sessionCount database = 0L) "capability denial happens before reads, hashing or session writes"
    for email, password in [ "member@example.test", "wrong"; "missing@example.test", "wrong"; "member@example.test", ""; "MEMBER@example.test", "secret123456" ] do
        assertNone handle email password
    check (sessionCount database = 0L) "all rejected credentials leave sessions unchanged"
    productionCalls.Clear()
    let successful = handle.Invoke(inputs "member@example.test" "secret123456")
    let token =
        match successful with
        | Ok result ->
            check (result.Console = []) "sign-in emits no console output"
            match result.Values with
            | [ ResultValue(TOption(TNamed "SessionRow"), TNamed "DbError", Ok(OptionValue(TNamed "SessionRow", Some(RecordValue("SessionRow", fields))))) ] ->
                check (fields.Count = 8) "session projection contains only its fixed public fields"
                check (fields["user-id"] = NamedValue("UserId", IntValue 1L)) "session owner is the authenticated user"
                check (fields["ip-address"] = OptionValue(TNamed "RemoteIp", Some(NamedValue("RemoteIp", StringValue "192.0.2.34")))) "request IP is carried into the typed session"
                check (fields["user-agent"] = OptionValue(TString, Some(StringValue "Synthetic SignIn Test"))) "request user-agent is carried into the session"
                for field in [ "last-active-at"; "created-at"; "updated-at" ] do check (fields[field] = NamedValue("UtcInstant", IntValue 1791806400000000L)) "all session timestamps use the explicit instant"
                match fields["token"] with
                | NamedValue("SessionToken", StringValue value) -> value
                | _ -> failwith "session token has wrong type"
            | _ -> failwith "successful sign-in has wrong return shape"
        | Error _ -> failwith "real sign-in failed"
    check (token.Length = 24 && (token |> Seq.forall (fun c -> "123456789ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz".Contains c))) "real token is 24 base58 characters"
    check (productionCalls |> Seq.toList = [ "campfire.users.authenticate-password"; "campfire.sessions.start" ]) "real sign-in authenticates before the sole session insert"
    check (sessionCount database = 1L && factories = 0) "normal sign-in writes once and never uses a test provider"
    check (raw.Invoke (HostOperationId "campfire.users.authenticate-password") [ NamedValue("SessionToken", StringValue "wrong"); StringValue "wrong" ] = Error HostOperationFailure.ProviderContractMismatch) "password operation rejects wrong input types"
    check (raw.Invoke (HostOperationId "campfire.sessions.start") [ RecordValue("SessionStart", Map.empty) ] = Error HostOperationFailure.ProviderContractMismatch) "session start rejects malformed records"
    check (raw.Invoke (HostOperationId "campfire.users.any?") [ IntValue 1L ] = Error HostOperationFailure.ProviderContractMismatch) "any-users operation rejects excess inputs"
    match raw.Invoke (HostOperationId "campfire.users.any?") [] with
    | Ok [ ResultValue(TBool, TNamed "DbError", Ok(BoolValue true)) ] -> check true "any-users converts its real SQLite result"
    | _ -> failwith "any-users host result has the wrong type"
    let restored = raw.Invoke (HostOperationId "campfire.sessions.find-by-token") [ NamedValue("SessionToken", StringValue token) ]
    match restored with
    | Ok [ ResultValue(TOption(TNamed "SessionRow"), TNamed "DbError", Ok(OptionValue(TNamed "SessionRow", Some _))) ] -> check true "issued token is immediately restorable from SQLite"
    | _ -> failwith "issued token cannot be restored"
    let reloadedAgain = makeEngine capabilities
    entry reloadedAgain |> ignore
    check (sessionCount database = 1L) "dictionary reload does not create another session"
    raw.Dispose()
    results.Count

[<EntryPoint>]
let main _ =
    Directory.CreateDirectory(proof) |> ignore
    let mutable passed = false
    let mutable cases = 0
    let mutable failure = None
    try
        cases <- run ()
        passed <- true
    with error -> failure <- Some(error.Message)
    let pins = JsonObject()
    for path in sourcePaths do
        let full = Path.Combine(root, path)
        if File.Exists full then pins[path] <- JsonValue.Create(hash full)
    let receipt = JsonObject()
    receipt["passed"] <- JsonValue.Create passed
    receipt["assertions"] <- JsonValue.Create assertions
    receipt["attachedCases"] <- JsonValue.Create cases
    receipt["sourceSha256"] <- pins
    receipt["failure"] <- failure |> Option.map JsonValue.Create |> Option.defaultValue null
    File.WriteAllText(Path.Combine(proof, "receipt.json"), receipt.ToJsonString(System.Text.Json.JsonSerializerOptions(WriteIndented = true)))
    if passed then
        printfn "Campfire sign-in integration passed: %d assertions / %d scoped cases; evidence: %s" assertions cases proof
        0
    else
        eprintfn "Campfire sign-in integration failed: %s; evidence: %s" (Option.defaultValue "unknown" failure) proof
        1
