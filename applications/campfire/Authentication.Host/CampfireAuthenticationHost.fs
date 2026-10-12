namespace AgentLang.Campfire.Authentication.Host

open System
open AgentLang
open AgentLang.Campfire.Authentication.Persistence
open AgentLang.Campfire.Persistence

exception private AdapterContractMismatch

module CampfireAuthenticationHost =
    let private sessionFindId = HostOperationId "campfire.sessions.find-by-token"
    let private sessionRefreshId = HostOperationId "campfire.sessions.refresh-before"
    let private sessionStartId = HostOperationId "campfire.sessions.start"
    let private userFindId = HostOperationId "campfire.users.find-by-id"
    let private userAnyId = HostOperationId "campfire.users.any?"
    let private botKeyId = HostOperationId "campfire.users.authenticate-bot-key"
    let private passwordAuthenticateId = HostOperationId "campfire.users.authenticate-password"
    let private ipBannedId = HostOperationId "campfire.bans.ip-banned?"
    let private roomFindId = HostOperationId "campfire.rooms.find-for-user"

    let operationIds =
        Set.ofList
            [ sessionFindId
              sessionRefreshId
              sessionStartId
              userFindId
              userAnyId
              botKeyId
              passwordAuthenticateId
              ipBannedId
              roomFindId ]

    let manifest =
        [ sessionFindId,
          { Id = sessionFindId
            WordName = "campfire.sessions.find-by-token"
            Inputs = [ TNamed "SessionToken" ]
            Outputs = [ TResult(TOption(TNamed "SessionRow"), TNamed "DbError") ]
            Effects = Set.singleton "db.read"
            Documentation = "Finds a persisted session by the exact already-verified Rails session token." }
          sessionRefreshId,
          { Id = sessionRefreshId
            WordName = "campfire.sessions.refresh-before"
            Inputs = [ TNamed "SessionRefresh" ]
            Outputs = [ TResult(TNamed "SessionRow", TNamed "DbError") ]
            Effects = Set.singleton "db.write"
            Documentation = "Conditionally refreshes one session before the Flow-selected UTC cutoff." }
          sessionStartId,
          { Id = sessionStartId
            WordName = "campfire.sessions.start"
            Inputs = [ TNamed "SessionStart" ]
            Outputs = [ TResult(TNamed "SessionRow", TNamed "DbError") ]
            Effects = Set.ofList [ "db.write"; "random.read" ]
            Documentation = "Creates a persisted session for the authenticated user and trusted request metadata." }
          userFindId,
          { Id = userFindId
            WordName = "campfire.users.find-by-id"
            Inputs = [ TNamed "UserId" ]
            Outputs = [ TResult(TOption(TNamed "AuthUser"), TNamed "DbError") ]
            Effects = Set.singleton "db.read"
            Documentation = "Finds a user by primary key without applying role or status filtering." }
          userAnyId,
          { Id = userAnyId
            WordName = "campfire.users.any?"
            Inputs = []
            Outputs = [ TResult(TBool, TNamed "DbError") ]
            Effects = Set.singleton "db.read"
            Documentation = "Reports whether the persisted users table contains any users." }
          botKeyId,
          { Id = botKeyId
            WordName = "campfire.users.authenticate-bot-key"
            Inputs = [ TNamed "BotKey" ]
            Outputs = [ TResult(TOption(TNamed "AuthUser"), TNamed "DbError") ]
            Effects = Set.singleton "db.read"
            Documentation = "Looks up an active bot by the exact bot key supplied by a trusted caller." }
          passwordAuthenticateId,
          { Id = passwordAuthenticateId
            WordName = "campfire.users.authenticate-password"
            Inputs = [ TString; TString ]
            Outputs = [ TResult(TOption(TNamed "AuthUser"), TNamed "DbError") ]
            Effects = Set.singleton "db.read"
            Documentation = "Authenticates an account using its supplied email address and password." }
          ipBannedId,
          { Id = ipBannedId
            WordName = "campfire.bans.ip-banned?"
            Inputs = [ TNamed "RemoteIp" ]
            Outputs = [ TResult(TBool, TNamed "DbError") ]
            Effects = Set.singleton "db.read"
            Documentation = "Checks the configured remote IP against the persisted ban table." }
          roomFindId,
          { Id = roomFindId
            WordName = "campfire.rooms.find-for-user"
            Inputs = [ TNamed "UserId"; TNamed "RoomId" ]
            Outputs = [ TResult(TOption(TNamed "RoomRow"), TNamed "DbError") ]
            Effects = Set.singleton "db.read"
            Documentation = "Finds a room only when the supplied user has a membership." } ]
        |> Map.ofList

    let private mismatch () = raise AdapterContractMismatch

    let private recordFields (expectedName: string) (expectedFieldNames: string list) (value: Value) : Map<string, Value> =
        match value with
        | RecordValue(actualName, fields)
            when actualName = expectedName
                 && fields.Count = List.length expectedFieldNames
                 && (expectedFieldNames |> List.forall fields.ContainsKey) -> fields
        | _ -> mismatch ()

    let private field (fields: Map<string, Value>) (name: string) =
        match fields.TryFind name with
        | Some value -> value
        | None -> mismatch ()

    let private asNamedInt expectedName = function
        | NamedValue(actualName, IntValue value) when actualName = expectedName -> value
        | _ -> mismatch ()

    let private asNamedString expectedName = function
        | NamedValue(actualName, StringValue value) when actualName = expectedName && not (isNull value) -> value
        | _ -> mismatch ()

    let private asInt64 = function
        | IntValue value -> value
        | _ -> mismatch ()

    let private asString = function
        | StringValue value when not (isNull value) -> value
        | _ -> mismatch ()

    let private asOptionString = function
        | OptionValue(TString, None) -> None
        | OptionValue(TString, Some(StringValue value)) when not (isNull value) -> Some value
        | _ -> mismatch ()

    let private asOptionRemoteIp = function
        | OptionValue(TNamed "RemoteIp", None) -> None
        | OptionValue(TNamed "RemoteIp", Some(NamedValue("RemoteIp", StringValue value))) when not (isNull value) -> Some(RemoteIp value)
        | _ -> mismatch ()

    let private fromUtcInstant = function
        | NamedValue("UtcInstant", IntValue unixMicros) ->
            let epochTicks = DateTime.UnixEpoch.Ticks
            let ticks = epochTicks + unixMicros * 10L
            DateTimeOffset(DateTime(ticks, DateTimeKind.Utc))
        | _ -> mismatch ()

    let private toUtcInstant (instant: DateTimeOffset) =
        let utcTicks = instant.ToUniversalTime().Ticks
        let microsecondTicks = utcTicks - utcTicks % 10L
        let unixMicros = (microsecondTicks - DateTime.UnixEpoch.Ticks) / 10L
        NamedValue("UtcInstant", IntValue unixMicros)

    let private sessionIdValue (SessionId value) = NamedValue("SessionId", IntValue value)
    let private userIdValue (UserId value) = NamedValue("UserId", IntValue value)
    let private remoteIpValue (RemoteIp value) = NamedValue("RemoteIp", StringValue value)

    let private roleValue = function
        | AuthUserRole.Member -> EnumValue("Role", "member")
        | AuthUserRole.Administrator -> EnumValue("Role", "administrator")
        | AuthUserRole.Bot -> EnumValue("Role", "bot")
        | _ -> mismatch ()

    let private statusValue = function
        | AuthUserStatus.Active -> EnumValue("AuthUserStatus", "active")
        | AuthUserStatus.Deactivated -> EnumValue("AuthUserStatus", "deactivated")
        | AuthUserStatus.Banned -> EnumValue("AuthUserStatus", "banned")
        | _ -> mismatch ()

    let private roomKindValue = function
        | RoomKind.Open -> EnumValue("RoomKind", "open")
        | RoomKind.Closed -> EnumValue("RoomKind", "closed")
        | RoomKind.Direct -> EnumValue("RoomKind", "direct")

    let private dbErrorValue = function
        | DbError.Unavailable -> EnumValue("DbError", "unavailable")
        | DbError.NotFound -> EnumValue("DbError", "not-found")
        | DbError.ConstraintConflict -> EnumValue("DbError", "constraint-conflict")
        | DbError.InvalidInput -> EnumValue("DbError", "invalid-input")

    let private authUserValue (user: AuthUser) =
        RecordValue(
            "AuthUser",
            Map.ofList
                [ "id", user.Id |> userIdValue
                  "name", StringValue user.Name
                  "role", roleValue user.Role
                  "status", statusValue user.Status ])

    let private sessionRowValue (row: AgentLang.Campfire.Authentication.Persistence.SessionRow) =
        let optionalIp =
            row.IpAddress
            |> Option.map (RemoteIp >> remoteIpValue)
            |> fun value -> OptionValue(TNamed "RemoteIp", value)
        RecordValue(
            "SessionRow",
            Map.ofList
                [ "id", (let (SessionId value) = row.Id in NamedValue("SessionId", IntValue value))
                  "user-id", row.UserId |> userIdValue
                  "token", (let (SessionToken value) = row.Token in NamedValue("SessionToken", StringValue value))
                  "ip-address", optionalIp
                  "user-agent", OptionValue(TString, Option.map StringValue row.UserAgent)
                  "last-active-at", toUtcInstant row.LastActiveAt
                  "created-at", toUtcInstant row.CreatedAt
                  "updated-at", toUtcInstant row.UpdatedAt ])

    let private roomRowValue (row: AgentLang.Campfire.Persistence.RoomRow) =
        RecordValue(
            "RoomRow",
            Map.ofList
                [ "id", (let (RoomId value) = row.Id in NamedValue("RoomId", IntValue value))
                  "name", OptionValue(TString, Option.map StringValue row.Name)
                  "kind", roomKindValue row.Kind
                  "creator-id", row.CreatorId |> userIdValue
                  "created-at", StringValue row.CreatedAt
                  "updated-at", StringValue row.UpdatedAt ])

    let private resultValue okType mapOk (result: Result<'T, DbError>) =
        ResultValue(okType, TNamed "DbError", result |> Result.map mapOk |> Result.mapError dbErrorValue)

    let private oneValue value = Ok [ value ]

    let private asSessionRefresh value =
        let fields = recordFields "SessionRefresh" [ "session-id"; "cutoff"; "now"; "ip-address"; "user-agent" ] value
        let sessionId = asNamedInt "SessionId" (field fields "session-id")
        { SessionId = SessionId sessionId
          Cutoff = fromUtcInstant (field fields "cutoff")
          Now = fromUtcInstant (field fields "now")
          IpAddress = field fields "ip-address" |> asOptionRemoteIp |> Option.map (fun (RemoteIp ip) -> ip)
          UserAgent = field fields "user-agent" |> asOptionString }

    let private asSessionStart value =
        let fields = recordFields "SessionStart" [ "user-id"; "now"; "ip-address"; "user-agent" ] value
        let userId = asNamedInt "UserId" (field fields "user-id")
        { UserId = UserId userId
          Now = fromUtcInstant (field fields "now")
          IpAddress = field fields "ip-address" |> asOptionRemoteIp |> Option.map (fun (RemoteIp ip) -> ip)
          UserAgent = field fields "user-agent" |> asOptionString }

    let private invoke (provider: AuthenticationSqliteProvider) operationId values =
        try
            match operationId, values with
            | id, [ tokenValue ] when id = sessionFindId ->
                let token = asNamedString "SessionToken" tokenValue |> SessionToken
                provider.FindSessionByToken token
                |> resultValue (TOption(TNamed "SessionRow")) (fun maybe -> OptionValue(TNamed "SessionRow", Option.map sessionRowValue maybe))
                |> oneValue
            | id, [ refreshValue ] when id = sessionRefreshId ->
                let refresh = asSessionRefresh refreshValue
                provider.RefreshSessionBefore refresh
                |> resultValue (TNamed "SessionRow") sessionRowValue
                |> oneValue
            | id, [ startValue ] when id = sessionStartId ->
                let start = asSessionStart startValue
                provider.StartSession start
                |> resultValue (TNamed "SessionRow") sessionRowValue
                |> oneValue
            | id, [ userValue ] when id = userFindId ->
                let (UserId userId) = UserId(asNamedInt "UserId" userValue)
                provider.FindUserById(UserId userId)
                |> resultValue (TOption(TNamed "AuthUser")) (fun maybe -> OptionValue(TNamed "AuthUser", Option.map authUserValue maybe))
                |> oneValue
            | id, [] when id = userAnyId ->
                provider.AnyUsers()
                |> resultValue TBool BoolValue
                |> oneValue
            | id, [ keyValue ] when id = botKeyId ->
                let key = asNamedString "BotKey" keyValue |> BotKey
                provider.AuthenticateBotKey key
                |> resultValue (TOption(TNamed "AuthUser")) (fun maybe -> OptionValue(TNamed "AuthUser", Option.map authUserValue maybe))
                |> oneValue
            | id, [ emailValue; passwordValue ] when id = passwordAuthenticateId ->
                let email = asString emailValue
                let password = asString passwordValue
                provider.AuthenticatePassword(email, password)
                |> resultValue (TOption(TNamed "AuthUser")) (fun maybe -> OptionValue(TNamed "AuthUser", Option.map authUserValue maybe))
                |> oneValue
            | id, [ ipValue ] when id = ipBannedId ->
                let ip = asNamedString "RemoteIp" ipValue |> RemoteIp
                provider.IsIpBanned ip
                |> resultValue TBool BoolValue
                |> oneValue
            | id, [ userValue; roomValue ] when id = roomFindId ->
                let (UserId userId) = UserId(asNamedInt "UserId" userValue)
                let (RoomId roomId) = RoomId(asNamedInt "RoomId" roomValue)
                provider.FindRoomForUser(UserId userId, RoomId roomId)
                |> resultValue (TOption(TNamed "RoomRow")) (fun maybe -> OptionValue(TNamed "RoomRow", Option.map roomRowValue maybe))
                |> oneValue
            | id, _ when operationIds.Contains id -> Error HostOperationFailure.ProviderContractMismatch
            | _ -> Error HostOperationFailure.MissingHandler
        with
        | AdapterContractMismatch -> Error HostOperationFailure.ProviderContractMismatch
        | _ -> Error HostOperationFailure.InvocationFailed

    let createProvider databasePath =
        AuthenticationSqliteProvider.OpenExisting(databasePath)
        |> Result.map (fun provider ->
            { OperationIds = operationIds
              Invoke = invoke provider
              Dispose = ignore })

    let createEnvironment productionProvider createTestProvider : HostOperationEnvironment =
        { Manifest = manifest
          ProductionProvider = productionProvider
          CreateTestProvider = createTestProvider }
