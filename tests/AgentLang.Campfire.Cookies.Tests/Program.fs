namespace AgentLang.Campfire.Cookies.Tests

open System
open System.Globalization
open System.IO
open System.Security.Cryptography
open System.Text
open System.Text.Json
open System.Text.Json.Nodes
open AgentLang.Campfire.Cookies

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

    let private group name action =
        action ()
        groups <- groups + 1

    let private stringProperty (name: string) (element: JsonElement) =
        let value = element.GetProperty(name)
        if value.ValueKind = JsonValueKind.Null then null else value.GetString()

    let private parseTime (value: string) =
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind)

    let private expectedString (element: JsonElement) =
        if element.ValueKind = JsonValueKind.String then Some(element.GetString()) else None

    let private expectedObject (element: JsonElement) =
        if element.ValueKind = JsonValueKind.Null then
            None
        else
            Some(JsonNode.Parse(element.GetRawText()).AsObject())

    let private assertNodeEqual expected actual message =
        assertions <- assertions + 1
        let equalNodes =
            match expected, actual with
            | None, None -> true
            | Some left, Some right -> JsonNode.DeepEquals(left, right)
            | _ -> false

        if not equalNodes then
            failwith $"{message}: expected {expected}, got {actual}"

    let private tamperPart partIndex (raw: string) =
        let parts = raw.Split([| "--" |], StringSplitOptions.None)
        if parts.Length <> 3 || parts[partIndex].Length = 0 then
            failwith "The Rails encrypted-cookie fixture shape was unexpected"
        let original = parts[partIndex]
        let replacement = if original[0] = 'A' then 'B' else 'A'
        parts[partIndex] <- string replacement + original.Substring(1)
        String.Join("--", parts)

    let private signedExpiryEnvelope secretKeyBase expiry =
        let innerMessage = Convert.ToBase64String(Encoding.UTF8.GetBytes("\"expiry-test\""))
        let payload =
            RailsCookieFormat.makeLegacyEnvelope innerMessage "cookie.session_token" (Some expiry)
        let encoded = Convert.ToBase64String(payload)
        let key = RailsKeyDerivation.deriveKey secretKeyBase "signed cookie" 64
        try
            let mac = HMACSHA1.HashData(key, Encoding.ASCII.GetBytes(encoded))
            encoded + "--" + Convert.ToHexString(mac).ToLowerInvariant()
        finally
            CryptographicOperations.ZeroMemory(key.AsSpan())
            CryptographicOperations.ZeroMemory(payload.AsSpan())

    [<EntryPoint>]
    let main _ =
        let fixturePath = Path.Combine(AppContext.BaseDirectory, "Fixtures", "rails-cookie-vectors.json")
        use fixtureDocument = JsonDocument.Parse(File.ReadAllText(fixturePath))
        let root = fixtureDocument.RootElement
        let secretKeyBase = stringProperty "secret_key_base" root
        let now = parseTime (stringProperty "now" root)

        use service =
            match CookieService.Create(secretKeyBase) with
            | Ok value -> value
            | Error _ -> failwith "The saved test configuration was rejected"

        group "Rails PBKDF2 key derivation" (fun () ->
            for vector in root.GetProperty("key_generator").EnumerateArray() do
                let key =
                    RailsKeyDerivation.deriveKey
                        secretKeyBase
                        (stringProperty "salt" vector)
                        (vector.GetProperty("length").GetInt32())
                let actual = Convert.ToHexString(key).ToLowerInvariant()
                CryptographicOperations.ZeroMemory(key.AsSpan())
                equal (stringProperty "key_hex" vector) actual "PBKDF2-HMAC-SHA256 output")

        group "Rails signed-cookie byte compatibility" (fun () ->
            for vector in root.GetProperty("signed_cookies").GetProperty("generate").EnumerateArray() do
                let created = service.SignSessionToken(stringProperty "value" vector, now)
                match created with
                | Error _ -> failwith "A valid Rails signed-cookie case could not be written"
                | Ok actual ->
                    equal (stringProperty "raw" vector) actual "Signed session_token bytes"
                    check (service.VerifySessionToken(actual, now.AddYears(20).AddMilliseconds(-1)).IsSome) "Signed session token expired before Rails expiry"
                    check (service.VerifySessionToken(actual, now.AddYears(20)).IsNone) "Signed session token remained valid at Rails expiry"

            for vector in root.GetProperty("signed_cookies").GetProperty("verify").EnumerateArray() do
                let vectorNow = parseTime (stringProperty "now" vector)
                let expected = expectedString (vector.GetProperty("expected"))
                let actual = service.VerifySessionToken(stringProperty "raw" vector, vectorNow)
                equal expected actual (stringProperty "case" vector))

        group "Rails encrypted session compatibility and state updates" (fun () ->
            for vector in root.GetProperty("encrypted_cookies").GetProperty("verify").EnumerateArray() do
                let vectorNow = parseTime (stringProperty "now" vector)
                let caseName = stringProperty "case" vector
                let expected =
                    if caseName = "short-lived before expiry" then None
                    else expectedObject (vector.GetProperty("expected"))
                let actual = service.DecryptRailsSession(stringProperty "raw" vector, vectorNow)
                assertNodeEqual expected actual caseName

            let generated = root.GetProperty("encrypted_cookies").GetProperty("generate")[0]
            let originalState = JsonNode.Parse(generated.GetProperty("value").GetRawText()).AsObject()
            let updatedState = JsonNode.Parse(generated.GetProperty("value").GetRawText()).AsObject()
            updatedState["flash"] <- JsonValue.Create("Message sent")

            match service.EncryptRailsSession(updatedState, now) with
            | Error _ -> failwith "A bounded session object could not be written"
            | Ok raw ->
                let expiry =
                    stringProperty "expires_at" generated
                    |> parseTime
                    |> RailsCookieFormat.formatExpiry
                equal
                    (Some(Some "cookie._campfire_session", Some expiry))
                    (service.InspectRailsSessionMetadata(raw))
                    "Encrypted session purpose and expiry"
                let decoded = service.DecryptRailsSession(raw, now)
                assertNodeEqual (Some updatedState) decoded "Updated Rails session round trip"
                let decodedState = decoded |> Option.defaultWith (fun () -> failwith "The updated session could not be decoded")
                check (JsonNode.DeepEquals(originalState["return_to_after_authenticating"], decodedState["return_to_after_authenticating"])) "The return-to URL was lost while updating Rails session state"
                check (JsonNode.DeepEquals(JsonValue.Create("Message sent"), decodedState["flash"])) "The updated flash value was lost"
                check (service.DecryptRailsSession(raw, now.AddYears(20).AddMilliseconds(-1)).IsSome) "Encrypted session expired before its Rails expiry"
                check (service.DecryptRailsSession(raw, now.AddYears(20)).IsNone) "Encrypted session remained valid at its Rails expiry"

                match service.EncryptRailsSession(updatedState, now) with
                | Ok secondRaw -> check (raw <> secondRaw) "AES-GCM output reused a production nonce"
                | Error _ -> failwith "A second bounded session object could not be written")

        group "Rack cookie escaping" (fun () ->
            for vector in root.GetProperty("cookie_escaping").EnumerateArray() do
                let rawValue = vector.GetProperty("raw")
                if rawValue.ValueKind <> JsonValueKind.Null then
                    match RackCookieEncoding.escape(rawValue.GetString()) with
                    | Ok actual -> equal (stringProperty "wire" vector) actual "Rack escape"
                    | Error _ -> failwith "A bounded Rack escape input was rejected"

                match RackCookieEncoding.unescape(stringProperty "wire" vector) with
                | Ok actual -> equal (stringProperty "parsed" vector) actual "Rack unescape"
                | Error _ -> failwith "A bounded Rack unescape input was rejected")

        group "Fail-closed cookie input and size limits" (fun () ->
            match CookieService.Create("short") with
            | Error CookieFailure.InvalidConfiguration -> check true "Short secret configuration"
            | _ -> failwith "Short secret configuration was not rejected with a stable failure"
            equal (Error CookieFailure.InvalidInput) (service.SignSessionToken(null, now)) "Null session token"
            equal (Error CookieFailure.InvalidInput) (service.EncryptRailsSession(null, now)) "Null session object"
            equal (Error CookieFailure.InvalidInput) (RackCookieEncoding.escape(null)) "Null Rack escape"
            equal
                (Error CookieFailure.CookieTooLarge)
                (RackCookieEncoding.unescape(String('x', RailsCookieFormat.MaxCookieWireBytes + 1)))
                "Oversized Rack input"

            let sessionNameBytes = Encoding.UTF8.GetByteCount("session_token")
            let rawAtExactCookieLimit = String('/', RailsCookieFormat.MaxCookieBytes - sessionNameBytes)
            check (RailsCookieFormat.cookieWithinLimit "session_token" rawAtExactCookieLimit) "Exact raw cookie name+value limit was rejected"
            let escapedAtExactRawLimit =
                match RackCookieEncoding.escape rawAtExactCookieLimit with
                | Ok value -> value
                | Error _ -> failwith "A raw-valid Rack value was rejected before escaping"
            check (escapedAtExactRawLimit.Length > RailsCookieFormat.MaxCookieBytes) "Rack wire expansion did not exceed the raw cookie bound"
            equal (Ok rawAtExactCookieLimit) (RackCookieEncoding.unescape escapedAtExactRawLimit) "Rack expanded wire round trip"
            check
                (not (RailsCookieFormat.cookieWithinLimit "session_token" (rawAtExactCookieLimit + "/")))
                "Raw cookie exceeding name+value limit was accepted"

            check (service.VerifySessionToken(String('x', 5000), now).IsNone) "Oversized signed cookie was accepted"
            check (service.DecryptRailsSession(String('x', 5000), now).IsNone) "Oversized encrypted cookie was accepted"
            equal (Error CookieFailure.CookieTooLarge) (service.SignSessionToken(String('x', 5000), now)) "Oversized signed-cookie output"

            let oversizedSession = JsonObject()
            oversizedSession["body"] <- JsonValue.Create(String('x', 5000))
            equal (Error CookieFailure.CookieTooLarge) (service.EncryptRailsSession(oversizedSession, now)) "Oversized encrypted-cookie output"

            let nestedSession = JsonObject()
            let mutable current = nestedSession
            for _ in 1..34 do
                let child = JsonObject()
                current["nested"] <- child
                current <- child
            equal (Error CookieFailure.InvalidSessionState) (service.EncryptRailsSession(nestedSession, now)) "Excessively nested session JSON"

            check (service.VerifySessionToken("not a cookie", now).IsNone) "Malformed signed cookie was accepted"
            check (service.DecryptRailsSession("not a cookie", now).IsNone) "Malformed encrypted cookie was accepted"

            for invalidExpiry in
                [ "2026-01-01T12:30:00"; "not-a-date"; "2026-01-01T12:30:00.12345678Z" ] do
                let raw = signedExpiryEnvelope secretKeyBase invalidExpiry
                check (service.VerifySessionToken(raw, now).IsNone) $"Invalid expiry was accepted: {invalidExpiry}")

        printfn "Passed %d assertions across %d groups." assertions groups
        0
