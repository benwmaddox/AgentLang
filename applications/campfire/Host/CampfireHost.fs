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
    let private csrfSessionSecretId = HostOperationId "campfire.csrf.session-secret"
    let private csrfFormTokenId = HostOperationId "campfire.csrf.form-token"
    let private csrfBoostTokensId = HostOperationId "campfire.csrf.boost-tokens"
    let private messagesCreateId = HostOperationId "campfire.messages.create"

    let operationIds =
        Set.ofList [ roomsPageId; membershipExistsId; messagesLastPageId; messagesLastPageWithCreatorsId; messagesCreateId
                     csrfSessionSecretId; csrfFormTokenId; csrfBoostTokensId ]

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
          csrfSessionSecretId,
          { Id = csrfSessionSecretId
            WordName = "campfire.csrf.session-secret"
            Inputs = [ TOption TString ]
            Outputs = [ TNamed "CsrfSecret" ]
            Effects = Set.singleton "random.read"
            Documentation = "The session's Rails _csrf_token when it decodes to 32 bytes; otherwise a new one (urlsafe Base64, 32 random bytes), marked fresh so the response stores it." }
          csrfFormTokenId,
          { Id = csrfFormTokenId
            WordName = "campfire.csrf.form-token"
            Inputs = [ TString; TString ]
            Outputs = [ TString ]
            Effects = Set.singleton "random.read"
            Documentation = "Rails' masked authenticity token: HMAC-SHA256(secret, identifier), XOR a fresh 32-byte pad, pad first, urlsafe Base64 without padding. The identifier is '<path>#<method>', or '!real_csrf_token' for the page's global token." }
          csrfBoostTokensId,
          { Id = csrfBoostTokensId
            WordName = "campfire.csrf.boost-tokens"
            Inputs = [ TString; TNamed "MessageId" ]
            Outputs = [ TNamed "BoostTokens" ]
            Effects = Set.singleton "random.read"
            Documentation = "Masked authenticity tokens for one message's eight quick-boost forms (/messages/:id/boosts#post): one HMAC, eight fresh pads." }
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

    let private decodeUrlSafe (text: string) =
        let padded = text.Replace('-', '+').Replace('_', '/')
        let padded = padded + String('=', (4 - padded.Length % 4) % 4)
        try Some(Convert.FromBase64String padded) with _ -> None

    let private encodeUrlSafe (bytes: byte array) =
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_')

    /// The session's raw CSRF secret: exactly 32 bytes, as Rails generates.
    let private csrfSecretBytes (text: string) =
        match decodeUrlSafe text with
        | Some bytes when bytes.Length = 32 -> Some bytes
        | _ -> None

    let private maskToken (digest: byte array) =
        let pad = RandomNumberGenerator.GetBytes 32
        Array.append pad (Array.map2 (^^^) pad digest) |> encodeUrlSafe

    let csrfFormToken (secret: string) (identifier: string) =
        match csrfSecretBytes secret with
        | Some raw -> maskToken (HMACSHA256.HashData(raw, Encoding.UTF8.GetBytes identifier))
        | None -> mismatch ()

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
            | id, [ candidateValue ] when id = csrfSessionSecretId ->
                let existing =
                    match asOptionString candidateValue with
                    | Some text when (csrfSecretBytes text).IsSome -> Some text
                    | _ -> None
                let value, fresh =
                    match existing with
                    | Some text -> text, false
                    | None -> encodeUrlSafe (RandomNumberGenerator.GetBytes 32), true
                Ok [ RecordValue("CsrfSecret", Map.ofList [ "value", StringValue value; "fresh", BoolValue fresh ]) ]
            | id, [ StringValue secret; StringValue identifier ] when id = csrfFormTokenId ->
                Ok [ StringValue(csrfFormToken secret identifier) ]
            | id, [ StringValue secret; NamedValue("MessageId", IntValue messageId) ] when id = csrfBoostTokensId ->
                let raw = csrfSecretBytes secret |> Option.defaultWith mismatch
                let digest = HMACSHA256.HashData(raw, Encoding.UTF8.GetBytes($"/messages/{messageId}/boosts#post"))
                let names = [ "thumbs-up"; "clapping"; "waving-hand"; "muscle"; "red-heart"; "tears-of-joy"; "party-popper"; "fire" ]
                Ok [ RecordValue("BoostTokens", names |> List.map (fun name -> name, StringValue(maskToken digest)) |> Map.ofList) ]
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
