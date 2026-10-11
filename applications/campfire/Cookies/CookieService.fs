namespace AgentLang.Campfire.Cookies

open System
open System.Globalization
open System.Security.Cryptography
open System.Text
open System.Text.Encodings.Web
open System.Text.Json
open System.Text.Json.Nodes

/// Stable, non-sensitive failures returned by cookie write operations.
[<RequireQualifiedAccess>]
type CookieFailure =
    | InvalidInput
    | InvalidConfiguration
    | InvalidSessionState
    | CookieTooLarge

module internal RailsKeyDerivation =
    let deriveKey (secretKeyBase: string) (salt: string) (length: int) =
        if isNull secretKeyBase || isNull salt || length <= 0 || length > 128 then
            invalidArg "length" "Invalid key derivation input"

        let secret = Encoding.UTF8.GetBytes(secretKeyBase)
        let saltBytes = Encoding.UTF8.GetBytes(salt)

        try
            Rfc2898DeriveBytes.Pbkdf2(secret, saltBytes, 1000, HashAlgorithmName.SHA256, length)
        finally
            CryptographicOperations.ZeroMemory(secret.AsSpan())
            CryptographicOperations.ZeroMemory(saltBytes.AsSpan())

module internal RailsCookieFormat =
    [<Literal>]
    let MaxCookieBytes = 4096

    [<Literal>]
    let MaxCookieWireBytes = 12288

    [<Literal>]
    let MaxJsonBytes = 16384

    [<Literal>]
    let MaxJsonInputEstimate = 65536

    [<Literal>]
    let MaxJsonDepth = 32

    let private utf8 = UTF8Encoding(false, false)

    let private jsonOptions =
        let options = JsonSerializerOptions()
        options.Encoder <- JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        options.WriteIndented <- false
        options.MaxDepth <- MaxJsonDepth
        options

    let private documentOptions =
        let mutable options = JsonDocumentOptions()
        options.MaxDepth <- MaxJsonDepth
        options.AllowTrailingCommas <- false
        options.CommentHandling <- JsonCommentHandling.Disallow
        options

    let private startsWithBytes (prefix: string) (bytes: byte array) =
        let prefixBytes = Encoding.ASCII.GetBytes(prefix)
        bytes.Length >= prefixBytes.Length
        && bytes.AsSpan(0, prefixBytes.Length).SequenceEqual(prefixBytes.AsSpan())

    let private escapeHtmlEntities (json: string) =
        json.Replace("<", "\\u003c").Replace(">", "\\u003e").Replace("&", "\\u0026")

    let private restoreRailsUnicodeSeparators (json: string) =
        let output = StringBuilder(json.Length)
        let mutable index = 0
        while index < json.Length do
            if json[index] = '\\' then
                let start = index
                while index < json.Length && json[index] = '\\' do index <- index + 1
                let slashCount = index - start
                let hasSeparatorEscape =
                    index + 5 <= json.Length
                    && json[index] = 'u'
                    && (json.Substring(index + 1, 4) = "2028" || json.Substring(index + 1, 4) = "2029")

                if slashCount % 2 = 1 && hasSeparatorEscape then
                    output.Append('\\', slashCount - 1) |> ignore
                    output.Append(if json.Substring(index + 1, 4) = "2028" then '\u2028' else '\u2029') |> ignore
                    index <- index + 5
                else
                    output.Append('\\', slashCount) |> ignore
            else
                output.Append(json[index]) |> ignore
                index <- index + 1

        output.ToString()

    let serializeNode (node: JsonNode) =
        let serialized = node.ToJsonString(jsonOptions) |> restoreRailsUnicodeSeparators |> escapeHtmlEntities
        utf8.GetBytes(serialized)

    let serializeString (value: string) =
        JsonValue.Create(value) |> serializeNode

    let private parseJson (bytes: byte array) =
        if isNull bytes || bytes.Length = 0 || bytes.Length > MaxJsonBytes then
            None
        else
            try
                use document = JsonDocument.Parse(ReadOnlyMemory<byte>(bytes), documentOptions)
                Some(document.RootElement.Clone())
            with _ ->
                None

    let private tryProperty (name: string) (element: JsonElement) =
        let mutable value = Unchecked.defaultof<JsonElement>
        if element.ValueKind = JsonValueKind.Object && element.TryGetProperty(name, &value) then Some value else None

    let private rubyToString (property: JsonElement option) =
        match property with
        | None -> Some ""
        | Some value ->
            match value.ValueKind with
            | JsonValueKind.Null -> Some ""
            | JsonValueKind.String -> Some(value.GetString())
            | JsonValueKind.Number -> Some(value.GetRawText())
            | JsonValueKind.True -> Some "true"
            | JsonValueKind.False -> Some "false"
            | _ -> Some "\u0000"

    let private parseExpiry (property: JsonElement option) =
        match property with
        | None -> Some None
        | Some value when value.ValueKind = JsonValueKind.Null -> Some None
        | Some value when value.ValueKind = JsonValueKind.String ->
            let text = value.GetString()
            let mutable parsed = DateTimeOffset.MinValue
            let zoneIndex =
                if text.EndsWith("Z", StringComparison.OrdinalIgnoreCase) then text.Length - 1
                else max (text.LastIndexOf('+')) (text.LastIndexOf('-'))
            let explicitZone =
                text.EndsWith("Z", StringComparison.OrdinalIgnoreCase)
                || (zoneIndex >= 19
                    && text.Length - zoneIndex = 6
                    && text[zoneIndex + 3] = ':'
                    && Char.IsDigit(text[zoneIndex + 1])
                    && Char.IsDigit(text[zoneIndex + 2])
                    && Char.IsDigit(text[zoneIndex + 4])
                    && Char.IsDigit(text[zoneIndex + 5]))
            let dateTimePart = if zoneIndex >= 0 then text.Substring(0, zoneIndex) else ""
            let fractionalDigits = dateTimePart.IndexOf('.')
            let fractionalPrecisionValid =
                if fractionalDigits < 0 then dateTimePart.Length = 19
                else
                    fractionalDigits = 19
                    && dateTimePart.Length - fractionalDigits - 1 >= 1
                    && dateTimePart.Length - fractionalDigits - 1 <= 7
                    && (dateTimePart.Substring(fractionalDigits + 1) |> Seq.forall Char.IsDigit)
            let isoShapeValid =
                text.Length >= 20
                && text[4] = '-'
                && text[7] = '-'
                && (text[10] = 'T' || text[10] = 't')
                && text[13] = ':'
                && text[16] = ':'
                && explicitZone
                && fractionalPrecisionValid

            // Rails emits millisecond UTC expiries. DateTimeOffset preserves at most seven fractional digits;
            // longer fractional precision is rejected instead of silently rounded.
            if isoShapeValid
               && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, &parsed) then
                Some(Some(parsed.ToUniversalTime()))
            else
                None
        | _ -> None

    type private EnvelopeParse =
        | NotLegacyEnvelope
        | InvalidLegacyEnvelope
        | LegacyEnvelope of message: string * purpose: string * expiresAt: DateTimeOffset option

    let private parseLegacyEnvelope (bytes: byte array) =
        if not (startsWithBytes "{\"_rails\":{\"message\":\"" bytes) then
            NotLegacyEnvelope
        else
            match parseJson bytes with
            | None -> InvalidLegacyEnvelope
            | Some root ->
                match tryProperty "_rails" root with
                | None -> InvalidLegacyEnvelope
                | Some rails when rails.ValueKind <> JsonValueKind.Object -> InvalidLegacyEnvelope
                | Some rails ->
                    match tryProperty "message" rails, rubyToString (tryProperty "pur" rails), parseExpiry (tryProperty "exp" rails) with
                    | Some message, Some purpose, Some expiry when message.ValueKind = JsonValueKind.String ->
                        LegacyEnvelope(message.GetString(), purpose, expiry)
                    | _ -> InvalidLegacyEnvelope

    let private isExpired (now: DateTimeOffset) (expiresAt: DateTimeOffset option) =
        match expiresAt with
        | Some expiry -> now.ToUniversalTime() >= expiry
        | None -> false

    let private strictBase64Decode (encoded: string) =
        if isNull encoded || encoded.Length % 4 <> 0 then
            None
        else
            try
                let decoded = Convert.FromBase64String(encoded)
                if Convert.ToBase64String(decoded) = encoded then Some decoded else None
            with _ ->
                None

    let private isBase64Character character =
        (character >= 'A' && character <= 'Z')
        || (character >= 'a' && character <= 'z')
        || (character >= '0' && character <= '9')
        || character = '+'
        || character = '/'
        || character = '-'
        || character = '_'
        || character = '='

    let urlSafeBase64Decode (encoded: string) =
        if isNull encoded || encoded.Length = 0 || (encoded |> Seq.exists (isBase64Character >> not)) then
            None
        else
            let firstPadding = encoded.IndexOf('=')
            let paddingCount = if firstPadding < 0 then 0 else encoded.Length - firstPadding
            let paddingIsValid =
                paddingCount <= 2
                && (firstPadding < 0 || firstPadding >= encoded.Length - 2)
                && (paddingCount = 0 || encoded.Length % 4 = 0)

            if not paddingIsValid then
                None
            else
                let translated = encoded.Replace('-', '+').Replace('_', '/')
                let remainder = translated.Length % 4
                if remainder = 1 then
                    None
                else
                    let normalized =
                        if paddingCount > 0 then translated
                        else translated + String('=', (4 - remainder) % 4)

                    match strictBase64Decode normalized with
                    | Some decoded -> Some decoded
                    | None -> None

    let private decodeLegacyMessage strict (message: string) =
        if strict then strictBase64Decode message else urlSafeBase64Decode message

    let elementToJsonObject (element: JsonElement) =
        if element.ValueKind <> JsonValueKind.Object then
            None
        else
            try
                JsonNode.Parse(element.GetRawText(), JsonNodeOptions(), documentOptions)
                |> Option.ofObj
                |> Option.map (fun node -> node.AsObject())
            with _ ->
                None

    let tryReadCookieJson (payload: byte array) (expectedPurpose: string option) (now: DateTimeOffset) strictMessageBase64 =
        if isNull payload || payload.Length > MaxJsonBytes * 2 then
            None
        else
            match parseLegacyEnvelope payload with
            | InvalidLegacyEnvelope -> None
            | NotLegacyEnvelope ->
                match expectedPurpose with
                | Some _ -> None
                | None -> parseJson payload
            | LegacyEnvelope(message, purpose, expiresAt) ->
                let purposeMatches = purpose = (expectedPurpose |> Option.defaultValue "")
                if not purposeMatches || isExpired now expiresAt then
                    None
                else
                    match decodeLegacyMessage strictMessageBase64 message with
                    | None -> None
                    | Some serializedValue -> parseJson serializedValue

    let makeLegacyEnvelope (message: string) (purpose: string) (expiresAt: string option) =
        let expiryJson =
            match expiresAt with
            | Some expiry -> "\"" + expiry + "\""
            | None -> "null"

        let payload =
            "{\"_rails\":{\"message\":\""
            + message
            + "\",\"exp\":"
            + expiryJson
            + ",\"pur\":\""
            + purpose
            + "\"}}"

        Encoding.UTF8.GetBytes(payload)

    let formatExpiry (time: DateTimeOffset) =
        time.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture)

    let expirationTwentyYearsFrom (now: DateTimeOffset) =
        now.ToUniversalTime().AddYears(20)

    let private isSafeRackByte value =
        (value >= byte 'a' && value <= byte 'z')
        || (value >= byte 'A' && value <= byte 'Z')
        || (value >= byte '0' && value <= byte '9')
        || value = byte '-'
        || value = byte '.'
        || value = byte '_'
        || value = byte '*'

    let escapeUnchecked (raw: string) =
        let bytes = Encoding.UTF8.GetBytes(raw)
        let output = StringBuilder(bytes.Length)
        for value in bytes do
            if isSafeRackByte value then
                output.Append(char value) |> ignore
            elif value = byte ' ' then
                output.Append('+') |> ignore
            else
                output.Append('%').Append(value.ToString("X2", CultureInfo.InvariantCulture)) |> ignore
        output.ToString()

    let cookieWithinLimit (name: string) (raw: string) =
        if isNull raw then
            false
        else
            try
                Encoding.UTF8.GetByteCount(name) + Encoding.UTF8.GetByteCount(raw) <= MaxCookieBytes
            with _ ->
                false

    let estimateNodeInput (root: JsonNode) =
        let mutable total = 0L
        let mutable valid = true

        let add amount =
            total <- total + amount
            if total > int64 MaxJsonInputEstimate then valid <- false

        let rec visit depth (node: JsonNode) =
            if not valid then ()
            elif depth > MaxJsonDepth then valid <- false
            elif isNull node then add 4L
            else
                match node with
                | :? JsonObject as objectNode ->
                    add 2L
                    for KeyValue(key, value) in objectNode do
                        add (int64 (Encoding.UTF8.GetByteCount(key) + 4))
                        visit (depth + 1) value
                | :? JsonArray as arrayNode ->
                    add 2L
                    for value in arrayNode do visit (depth + 1) value
                | :? JsonValue as valueNode ->
                    let mutable stringValue = Unchecked.defaultof<string>
                    if valueNode.TryGetValue<string>(&stringValue) then
                        add (int64 (Encoding.UTF8.GetByteCount(stringValue) + 2))
                    else
                        add 32L
                | _ -> valid <- false

        visit 0 root
        if valid then Some total else None

    let encryptAesGcm (key: byte array) (plaintext: byte array) =
        let nonce = Array.zeroCreate<byte> 12
        let ciphertext = Array.zeroCreate<byte> plaintext.Length
        let tag = Array.zeroCreate<byte> 16
        RandomNumberGenerator.Fill(nonce.AsSpan())
        use aes = new AesGcm(key, tag.Length)
        aes.Encrypt(nonce.AsSpan(), plaintext.AsSpan(), ciphertext.AsSpan(), tag.AsSpan(), ReadOnlySpan<byte>.Empty)
        Convert.ToBase64String(ciphertext)
        + "--"
        + Convert.ToBase64String(nonce)
        + "--"
        + Convert.ToBase64String(tag)

    let decryptAesGcm (key: byte array) (raw: string) =
        if isNull raw then
            None
        else
            let parts = raw.Split([| "--" |], StringSplitOptions.None)
            if parts.Length <> 3 then
                None
            else
                match strictBase64Decode parts[0], strictBase64Decode parts[1], strictBase64Decode parts[2] with
                | Some ciphertext, Some nonce, Some tag when nonce.Length = 12 && tag.Length = 16 ->
                    try
                        let plaintext = Array.zeroCreate<byte> ciphertext.Length
                        use aes = new AesGcm(key, tag.Length)
                        aes.Decrypt(nonce.AsSpan(), ciphertext.AsSpan(), tag.AsSpan(), plaintext.AsSpan(), ReadOnlySpan<byte>.Empty)
                        Some plaintext
                    with _ ->
                        None
                | _ -> None

    let tryGetEnvelopeForInspection (plaintext: byte array) =
        match parseLegacyEnvelope plaintext with
        | LegacyEnvelope(_, purpose, expiresAt) ->
            let expiry = expiresAt |> Option.map formatExpiry
            Some(Some purpose, expiry)
        | _ -> None

module RackCookieEncoding =
    /// Rack's `URI.encode_www_form_component` encoding for a cookie value.
    let escape (raw: string) =
        if isNull raw then
            Error CookieFailure.InvalidInput
        else
            try
                if Encoding.UTF8.GetByteCount(raw) <= RailsCookieFormat.MaxCookieBytes then
                    let wire = RailsCookieFormat.escapeUnchecked raw
                    Ok wire
                else
                    Error CookieFailure.CookieTooLarge
            with _ ->
                Error CookieFailure.InvalidInput

    /// Rack's cookie parsing behavior: `+` means space and malformed percent escapes are kept verbatim.
    let unescape (wire: string) =
        if isNull wire then
            Error CookieFailure.InvalidInput
        elif Encoding.UTF8.GetByteCount(wire) > RailsCookieFormat.MaxCookieWireBytes then
            Error CookieFailure.CookieTooLarge
        else
            try
                let bytes = Encoding.UTF8.GetBytes(wire)
                let output = ResizeArray<byte>(bytes.Length)
                let mutable index = 0
                let mutable malformed = false
                let hexValue value =
                    if value >= byte '0' && value <= byte '9' then int value - int '0'
                    elif value >= byte 'a' && value <= byte 'f' then int value - int 'a' + 10
                    elif value >= byte 'A' && value <= byte 'F' then int value - int 'A' + 10
                    else -1

                while index < bytes.Length && not malformed do
                    match bytes[index] with
                    | value when value = byte '+' ->
                        output.Add(byte ' ')
                        index <- index + 1
                    | value when value = byte '%' ->
                        if index + 2 >= bytes.Length then
                            malformed <- true
                        else
                            let high = hexValue bytes[index + 1]
                            let low = hexValue bytes[index + 2]
                            if high < 0 || low < 0 then
                                malformed <- true
                            else
                                output.Add(byte (high * 16 + low))
                                index <- index + 3
                    | value ->
                        output.Add(value)
                        index <- index + 1

                if malformed then Ok wire
                else Ok(UTF8Encoding(false, false).GetString(output.ToArray()))
            with _ ->
                Error CookieFailure.InvalidInput

[<Sealed>]
type CookieService private (signingKey: byte array, encryptionKey: byte array) =
    let mutable disposed = false

    let permanentExpiry (now: DateTimeOffset) =
        RailsCookieFormat.expirationTwentyYearsFrom now |> RailsCookieFormat.formatExpiry

    let signPayload payload =
        let encoded = Convert.ToBase64String(payload)
        let mac = HMACSHA1.HashData(signingKey, Encoding.ASCII.GetBytes(encoded))
        encoded + "--" + Convert.ToHexString(mac).ToLowerInvariant()

    let verifyMac (encoded: string) (digest: string) =
        try
            let mac = HMACSHA1.HashData(signingKey, Encoding.ASCII.GetBytes(encoded))
            let expected = Encoding.ASCII.GetBytes(Convert.ToHexString(mac).ToLowerInvariant())
            let actual = Encoding.ASCII.GetBytes(digest)
            CryptographicOperations.FixedTimeEquals(expected.AsSpan(), actual.AsSpan())
        with _ ->
            false

    let verifySignedRaw (raw: string) (now: DateTimeOffset) =
        if isNull raw || not (RailsCookieFormat.cookieWithinLimit "session_token" raw) || raw.Length < 42 then
            None
        else
            let separatorIndex = raw.Length - 42
            if raw.Substring(separatorIndex, 2) <> "--" then
                None
            else
                let encoded = raw.Substring(0, separatorIndex)
                let digest = raw.Substring(separatorIndex + 2)
                if String.IsNullOrWhiteSpace(encoded) || String.IsNullOrWhiteSpace(digest) || not (verifyMac encoded digest) then
                    None
                else
                    match RailsCookieFormat.urlSafeBase64Decode encoded with
                    | None -> None
                    | Some payload ->
                        let purpose = Some "cookie.session_token"
                        match RailsCookieFormat.tryReadCookieJson payload purpose now false with
                        | Some value -> Some value
                        | None -> RailsCookieFormat.tryReadCookieJson payload None now false

    let decryptSessionPayload raw now =
        if disposed || isNull raw || not (RailsCookieFormat.cookieWithinLimit "_campfire_session" raw) then
            None
        else
            match RailsCookieFormat.decryptAesGcm encryptionKey raw with
            | None -> None
            | Some plaintext ->
                try
                    let purpose = Some "cookie._campfire_session"
                    match RailsCookieFormat.tryReadCookieJson plaintext purpose now true with
                    | Some value -> Some value
                    | None -> RailsCookieFormat.tryReadCookieJson plaintext None now true
                finally
                    CryptographicOperations.ZeroMemory(plaintext.AsSpan())

    /// Derive the Rails cookie keys from host configuration. The synthetic upstream secret is test data only.
    static member Create(secretKeyBase: string) =
        if isNull secretKeyBase || Encoding.UTF8.GetByteCount(secretKeyBase) < 30 then
            Error CookieFailure.InvalidConfiguration
        else
            try
                let signingKey = RailsKeyDerivation.deriveKey secretKeyBase "signed cookie" 64
                try
                    let encryptionKey = RailsKeyDerivation.deriveKey secretKeyBase "authenticated encrypted cookie" 32
                    Ok(new CookieService(signingKey, encryptionKey))
                with _ ->
                    CryptographicOperations.ZeroMemory(signingKey.AsSpan())
                    Error CookieFailure.InvalidConfiguration
            with _ ->
                Error CookieFailure.InvalidConfiguration

    /// Creates the raw Rails `session_token` value with a 20-calendar-year expiry.
    member _.SignSessionToken(token: string, now: DateTimeOffset) =
        if disposed then
            Error CookieFailure.InvalidConfiguration
        elif isNull token then
            Error CookieFailure.InvalidInput
        elif Encoding.UTF8.GetByteCount(token) > RailsCookieFormat.MaxJsonBytes then
            Error CookieFailure.CookieTooLarge
        else
            try
                let tokenJson = RailsCookieFormat.serializeString token
                let message = Convert.ToBase64String(tokenJson)
                let envelope =
                    RailsCookieFormat.makeLegacyEnvelope message "cookie.session_token" (Some(permanentExpiry now))
                let raw = signPayload envelope
                if RailsCookieFormat.cookieWithinLimit "session_token" raw then Ok raw else Error CookieFailure.CookieTooLarge
            with _ ->
                Error CookieFailure.InvalidInput

    /// Verifies the fixed Rails `session_token` cookie. Invalid or expired values return `None`.
    member _.VerifySessionToken(raw: string, now: DateTimeOffset) =
        if disposed then None
        else
            try
                match verifySignedRaw raw now with
                | Some value when value.ValueKind = JsonValueKind.String -> Some(value.GetString())
                | _ -> None
            with _ ->
                None

    /// Encrypts a JSON object for the fixed Rails `_campfire_session` cookie.
    member _.EncryptRailsSession(session: JsonObject, now: DateTimeOffset) =
        if disposed then
            Error CookieFailure.InvalidConfiguration
        elif isNull session then
            Error CookieFailure.InvalidInput
        else
            match RailsCookieFormat.estimateNodeInput session with
            | None -> Error CookieFailure.InvalidSessionState
            | Some estimate when estimate > int64 RailsCookieFormat.MaxJsonInputEstimate -> Error CookieFailure.CookieTooLarge
            | Some _ ->
                try
                    let jsonBytes = RailsCookieFormat.serializeNode session
                    if jsonBytes.Length > RailsCookieFormat.MaxJsonBytes then
                        CryptographicOperations.ZeroMemory(jsonBytes.AsSpan())
                        Error CookieFailure.CookieTooLarge
                    else
                        let innerMessage = Convert.ToBase64String(jsonBytes)
                        CryptographicOperations.ZeroMemory(jsonBytes.AsSpan())
                        let envelope =
                            RailsCookieFormat.makeLegacyEnvelope innerMessage "cookie._campfire_session" (Some(permanentExpiry now))

                        try
                            let raw = RailsCookieFormat.encryptAesGcm encryptionKey envelope
                            if RailsCookieFormat.cookieWithinLimit "_campfire_session" raw then Ok raw else Error CookieFailure.CookieTooLarge
                        finally
                            CryptographicOperations.ZeroMemory(envelope.AsSpan())
                with _ ->
                    Error CookieFailure.InvalidSessionState

    /// Decrypts the fixed Rails `_campfire_session` cookie. Invalid, expired, or non-object values return `None`.
    member _.DecryptRailsSession(raw: string, now: DateTimeOffset) =
        if disposed then None
        else
            try
                match decryptSessionPayload raw now with
                | Some value -> RailsCookieFormat.elementToJsonObject value
                | None -> None
            with _ ->
                None

    /// Internal test inspection of the authenticated envelope; no plaintext session data is returned.
    member internal _.InspectRailsSessionMetadata(raw: string) =
        if disposed || isNull raw || not (RailsCookieFormat.cookieWithinLimit "_campfire_session" raw) then
            None
        else
            match RailsCookieFormat.decryptAesGcm encryptionKey raw with
            | None -> None
            | Some plaintext ->
                try RailsCookieFormat.tryGetEnvelopeForInspection plaintext
                finally CryptographicOperations.ZeroMemory(plaintext.AsSpan())

    interface IDisposable with
        member _.Dispose() =
            if not disposed then
                disposed <- true
                CryptographicOperations.ZeroMemory(signingKey.AsSpan())
                CryptographicOperations.ZeroMemory(encryptionKey.AsSpan())
