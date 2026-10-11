namespace AgentLang.Campfire.Host

open AgentLang
open AgentLang.Campfire.Persistence

exception private AdapterContractMismatch

module CampfireHost =
    let private roomsPageId = HostOperationId "campfire.rooms.page-for-user"
    let private membershipExistsId = HostOperationId "campfire.membership.exists?"
    let private messagesLastPageId = HostOperationId "campfire.messages.last-page"
    let private messagesCreateId = HostOperationId "campfire.messages.create"

    let operationIds =
        Set.ofList [ roomsPageId; membershipExistsId; messagesLastPageId; messagesCreateId ]

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
          messagesCreateId,
          { Id = messagesCreateId
            WordName = "campfire.messages.create"
            Inputs = [ TNamed "NewMessage" ]
            Outputs = [ TResult(TNamed "MessageRow", TNamed "DbError") ]
            Effects = Set.ofList [ "db.write"; "clock.read" ]
            Documentation = "Persists one message and optional rich-text body atomically." } ]
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

    let private asString = function
        | StringValue value when not (isNull value) -> value
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
          ClientMessageId = field fields "client-message-id" |> asString
          Body = field fields "body" |> asOptionString }

    let private roomKindValue = function
        | Open -> EnumValue("RoomKind", "open")
        | Closed -> EnumValue("RoomKind", "closed")
        | Direct -> EnumValue("RoomKind", "direct")

    let private dbErrorValue = function
        | Unavailable -> EnumValue("DbError", "unavailable")
        | NotFound -> EnumValue("DbError", "not-found")
        | ConstraintConflict -> EnumValue("DbError", "constraint-conflict")
        | InvalidInput -> EnumValue("DbError", "invalid-input")

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

    let private resultValue okType (result: Result<'T, DbError>) mapOk =
        ResultValue(okType, TNamed "DbError", result |> Result.map mapOk |> Result.mapError dbErrorValue)

    let private invoke (store: SqliteProvider) operationId values =
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
            | id, [ messageValue ] when id = messagesCreateId ->
                let result = store.CreateMessage(asNewMessage messageValue)
                Ok [ resultValue (TNamed "MessageRow") result messageRowValue ]
            | id, _ when operationIds.Contains id -> Error HostOperationFailure.ProviderContractMismatch
            | _ -> Error HostOperationFailure.MissingHandler
        with
        | AdapterContractMismatch -> Error HostOperationFailure.ProviderContractMismatch
        | _ -> Error HostOperationFailure.InvocationFailed

    let createProvider databasePath (clock: IClock) =
        SqliteProvider.OpenExisting(databasePath, clock)
        |> Result.map (fun store ->
            { OperationIds = operationIds
              Invoke = invoke store
              Dispose = ignore })

    let createEnvironment productionProvider createTestProvider : HostOperationEnvironment =
        { Manifest = manifest
          ProductionProvider = productionProvider
          CreateTestProvider = createTestProvider }
