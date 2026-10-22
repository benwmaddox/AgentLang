namespace AgentLang.Campfire.Host

open System
open System.Security.Cryptography
open System.Text
open AgentLang
open AgentLang.Campfire.Persistence

exception private AdapterContractMismatch

module CampfireHost =
    let private roomsPageId = HostOperationId "campfire.rooms.page-for-user"
    let private membershipExistsId = HostOperationId "campfire.membership.exists?"
    let private messagesLastPageId = HostOperationId "campfire.messages.last-page"
    let private messagesLastPageWithCreatorsId = HostOperationId "campfire.messages.last-page-with-creators"
    let private messagesCreateId = HostOperationId "campfire.messages.create"

    let operationIds =
        Set.ofList [ roomsPageId; membershipExistsId; messagesLastPageId; messagesLastPageWithCreatorsId; messagesCreateId ]

    let manifest =
        [ roomsPageId,
          { Id = roomsPageId
            WordName = "campfire.rooms.page-for-user"
            Inputs = [ TNamed "UserId"; TOption(TNamed "RoomId"); TInt ]
            Outputs = [ TResult(TList(TNamed "RoomRow"), TNamed "DbError") ]
            Effects = Set.singleton "db.read"
            Documentation = "Returns a bounded keyset page of rooms joined to this user's memberships." }
          membershipExistsId,
          { Id = membershipExistsId
            WordName = "campfire.membership.exists?"
            Inputs = [ TNamed "MembershipKey" ]
            Outputs = [ TResult(TBool, TNamed "DbError") ]
            Effects = Set.singleton "db.read"
            Documentation = "Checks the exact user and room membership key." }
          messagesLastPageId,
          { Id = messagesLastPageId
            WordName = "campfire.messages.last-page"
            Inputs = [ TNamed "RoomId" ]
            Outputs = [ TResult(TList(TNamed "MessageRow"), TNamed "DbError") ]
            Effects = Set.singleton "db.read"
            Documentation = "Returns the room's last bounded message page in oldest-first order." }
          messagesLastPageWithCreatorsId,
          { Id = messagesLastPageWithCreatorsId
            WordName = "campfire.messages.last-page-with-creators"
            Inputs = [ TNamed "RoomId" ]
            Outputs = [ TResult(TList(TNamed "MessageWithCreator"), TNamed "DbError") ]
            Effects = Set.singleton "db.read"
            Documentation = "Returns the room's last bounded message page, oldest first, with each creator's name, bio, update time and signed avatar token." }
          messagesCreateId,
          { Id = messagesCreateId
            WordName = "campfire.messages.create"
            Inputs = [ TNamed "NewMessage" ]
            Outputs = [ TResult(TNamed "MessageRow", TNamed "DbError") ]
            Effects = Set.ofList [ "db.write"; "clock.read"; "random.read" ]
            Documentation = "Persists one message and optional rich-text body atomically; generates a client ID only when absent." } ]
        |> Map.ofList

    let private mismatch () = raise AdapterContractMismatch

    let private recordFields (expectedName: string) (expectedFieldNames: string list) (value: Value) : Map<string, Value> =
        match value with
        | RecordValue(actualName, fields)
            when actualName = expectedName
                 && fields.Count = List.length expectedFieldNames
                 && (expectedFieldNames |> List.forall fields.ContainsKey) -> fields
        | _ -> mismatch ()

    let private field (fields: Map<string, Value>) (name: string) : Value =
        match fields.TryFind name with
        | Some value -> value
        | None -> mismatch ()

    let private asUserId = function
        | NamedValue("UserId", IntValue value) -> UserId value
        | _ -> mismatch ()

    let private asRoomId = function
        | NamedValue("RoomId", IntValue value) -> RoomId value
        | _ -> mismatch ()

    let private asInt64 = function
        | IntValue value -> value
        | _ -> mismatch ()

    let private asOptionString = function
        | OptionValue(TString, None) -> None
        | OptionValue(TString, Some(StringValue value)) when not (isNull value) -> Some value
        | _ -> mismatch ()

    let private asOptionRoomId = function
        | OptionValue(TNamed "RoomId", None) -> None
        | OptionValue(TNamed "RoomId", Some value) -> Some(asRoomId value)
        | _ -> mismatch ()

    let private asMembershipKey value =
        let fields = recordFields "MembershipKey" [ "user-id"; "room-id" ] value
        { UserId = field fields "user-id" |> asUserId
          RoomId = field fields "room-id" |> asRoomId }

    let private asNewMessage value =
        let fields = recordFields "NewMessage" [ "room-id"; "creator-id"; "client-message-id"; "body" ] value
        { RoomId = field fields "room-id" |> asRoomId
          CreatorId = field fields "creator-id" |> asUserId
          ClientMessageId = field fields "client-message-id" |> asOptionString
          Body = field fields "body" |> asOptionString }

    let private roomKindValue = function
        | Open -> EnumValue("RoomKind", "open", None)
        | Closed -> EnumValue("RoomKind", "closed", None)
        | Direct -> EnumValue("RoomKind", "direct", None)

    let private dbErrorValue = function
        | Unavailable -> EnumValue("DbError", "unavailable", None)
        | NotFound -> EnumValue("DbError", "not-found", None)
        | ConstraintConflict -> EnumValue("DbError", "constraint-conflict", None)
        | InvalidInput -> EnumValue("DbError", "invalid-input", None)

    let private roomRowValue (row: AgentLang.Campfire.Persistence.RoomRow) =
        RecordValue(
            "RoomRow",
            Map.ofList
                [ "id", (let (RoomId value) = row.Id in NamedValue("RoomId", IntValue value))
                  "name", OptionValue(TString, Option.map StringValue row.Name)
                  "kind", roomKindValue row.Kind
                  "creator-id", (let (UserId value) = row.CreatorId in NamedValue("UserId", IntValue value))
                  "created-at", StringValue row.CreatedAt
                  "updated-at", StringValue row.UpdatedAt ])

    let private messageRowValue (row: AgentLang.Campfire.Persistence.MessageRow) =
        RecordValue(
            "MessageRow",
            Map.ofList
                [ "id", (let (MessageId value) = row.Id in NamedValue("MessageId", IntValue value))
                  "room-id", (let (RoomId value) = row.RoomId in NamedValue("RoomId", IntValue value))
                  "creator-id", (let (UserId value) = row.CreatorId in NamedValue("UserId", IntValue value))
                  "client-message-id", StringValue row.ClientMessageId
                  "body", OptionValue(TString, Option.map StringValue row.Body)
                  "created-at", StringValue row.CreatedAt
                  "updated-at", StringValue row.UpdatedAt ])

    /// Rails `user.signed_id(purpose: :avatar)`: unpadded base64url of the
    /// signed_id JSON envelope, "--", and its hex HMAC-SHA256 under
    /// PBKDF2(secret_key_base, "active_record/signed_id", 1000, 64).
    let avatarSigningKey (secretKeyBase: string) =
        Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes secretKeyBase, Encoding.UTF8.GetBytes "active_record/signed_id", 1000, HashAlgorithmName.SHA256, 64)

    let avatarToken (key: byte array) (userId: int64) =
        let json = "{\"_rails\":{\"data\":" + string userId + ",\"pur\":\"user/avatar\"}}"
        let encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes json).TrimEnd('=').Replace('+', '-').Replace('/', '_')
        let digest = HMACSHA256.HashData(key, Encoding.ASCII.GetBytes encoded)
        encoded + "--" + Convert.ToHexString(digest).ToLowerInvariant()

    let private messageWithCreatorValue (key: byte array) (row: AgentLang.Campfire.Persistence.MessageWithCreator) =
        let (UserId creator) = row.CreatorId
        RecordValue(
            "MessageWithCreator",
            Map.ofList
                [ "id", (let (MessageId value) = row.Id in NamedValue("MessageId", IntValue value))
                  "room-id", (let (RoomId value) = row.RoomId in NamedValue("RoomId", IntValue value))
                  "creator-id", NamedValue("UserId", IntValue creator)
                  "created-at-ms", IntValue row.CreatedAtMs
                  "updated-at-ms", IntValue row.UpdatedAtMs
                  "client-message-id", StringValue row.ClientMessageId
                  "created-at", StringValue row.CreatedAt
                  "creator-name", StringValue row.CreatorName
                  "creator-bio", StringValue row.CreatorBio
                  "creator-updated-at", StringValue row.CreatorUpdatedAt
                  "creator-avatar-token", StringValue(avatarToken key creator)
                  "body", OptionValue(TString, Option.map StringValue row.Body) ])

    let private resultValue okType (result: Result<'T, DbError>) mapOk =
        ResultValue(okType, TNamed "DbError", result |> Result.map mapOk |> Result.mapError dbErrorValue)

    let private invoke (store: SqliteProvider) (avatarKey: byte array) operationId values =
        try
            match operationId, values with
            | id, [ userIdValue; afterRoomIdValue; pageSizeValue ] when id = roomsPageId ->
                let userId = asUserId userIdValue
                let afterRoomId = asOptionRoomId afterRoomIdValue
                let pageSize = asInt64 pageSizeValue
                let result = store.PageRoomsForUser(userId, afterRoomId, pageSize) |> Result.map (List.map roomRowValue)
                Ok [ resultValue (TList(TNamed "RoomRow")) result (fun rows -> ListValue(TNamed "RoomRow", rows)) ]
            | id, [ keyValue ] when id = membershipExistsId ->
                let result = store.MembershipExists(asMembershipKey keyValue)
                Ok [ resultValue TBool result BoolValue ]
            | id, [ roomIdValue ] when id = messagesLastPageId ->
                let result = store.LastPage(asRoomId roomIdValue) |> Result.map (List.map messageRowValue)
                Ok [ resultValue (TList(TNamed "MessageRow")) result (fun rows -> ListValue(TNamed "MessageRow", rows)) ]
            | id, [ roomIdValue ] when id = messagesLastPageWithCreatorsId ->
                let result = store.LastPageWithCreators(asRoomId roomIdValue) |> Result.map (List.map (messageWithCreatorValue avatarKey))
                Ok [ resultValue (TList(TNamed "MessageWithCreator")) result (fun rows -> ListValue(TNamed "MessageWithCreator", rows)) ]
            | id, [ messageValue ] when id = messagesCreateId ->
                let result = store.CreateMessage(asNewMessage messageValue)
                Ok [ resultValue (TNamed "MessageRow") result messageRowValue ]
            | id, _ when operationIds.Contains id -> Error HostOperationFailure.ProviderContractMismatch
            | _ -> Error HostOperationFailure.MissingHandler
        with
        | AdapterContractMismatch -> Error HostOperationFailure.ProviderContractMismatch
        | _ -> Error HostOperationFailure.InvocationFailed

    /// The secret signs avatar tokens; its key is derived once per provider.
    let createProviderWithGuidSource databasePath (clock: IClock) (guidSource: unit -> Guid) (secretKeyBase: string) =
        let avatarKey = avatarSigningKey secretKeyBase
        SqliteProvider.OpenExisting(databasePath, clock, guidSource)
        |> Result.map (fun store ->
            { OperationIds = operationIds
              Invoke = invoke store avatarKey
              Dispose = ignore })

    let createProvider databasePath (clock: IClock) (secretKeyBase: string) =
        createProviderWithGuidSource databasePath clock (fun () -> Guid.NewGuid()) secretKeyBase

    let createEnvironment productionProvider createTestProvider : HostOperationEnvironment =
        { Manifest = manifest
          ProductionProvider = productionProvider
          CreateTestProvider = createTestProvider }
