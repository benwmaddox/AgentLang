namespace CampfireHttpCookieParity

open System
open System.Collections.Generic
open System.Globalization
open System.IO
open System.Net
open System.Net.Http
open System.Net.Http.Headers
open System.Security.Cryptography
open System.Text
open System.Text.Json
open System.Text.Json.Nodes
open AgentLang.Campfire.Cookies

exception private CheckFailure of string

type private Cookie =
    { Name: string
      WireValue: string
      Value: string
      Attributes: Map<string, string>
      ExpiresHeader: string option
      ExpiresUtc: DateTimeOffset option
      ExpiresRfcValid: bool option
      ExpiresWeekday: string option
      ExpiresCalendarWeekday: string option
      SessionKeys: string list
      SessionCanonical: string option
      ReturnToPath: string option
      ReturnToOriginMatches: bool option
      FlashAlert: string option
      FlashDiscardCount: int option
      LastRoom: string option
      VerifiedSessionToken: string option }

type private Observation =
    { Host: string
      CaseName: string
      Status: int
      ContentType: string option
      Location: string option
      BodyBytes: int
      ContentLength: int64 option
      CacheControl: string option
      ContentTypeOptions: string option
      Cookies: Cookie list }

type private CaseSpec =
    { Name: string
      Method: string
      Path: string
      CookieHeader: string option
      ExpectedStatus: int
      ExpectedLocation: string option
      ExpectedCookieNames: string list
      ExpectedBodyBytes: int option }

module Program =
    let mutable private assertionCount = 0

    let private check label condition =
        assertionCount <- assertionCount + 1
        if not condition then raise (CheckFailure label)

    let private fail label = raise (CheckFailure label)

    let private getValues (headers: HttpHeaders) (name: string) =
        let mutable values = Unchecked.defaultof<IEnumerable<string>>
        if headers.TryGetValues(name, &values) then values |> Seq.toList else []

    let private getString (node: JsonNode) =
        if isNull node then None
        else
            try Some(node.GetValue<string>())
            with _ -> None

    let private getObjectValue (node: JsonNode) (key: string) =
        match node with
        | :? JsonObject as value -> value[key]
        | _ -> null

    let private getArrayCount (node: JsonNode) =
        match node with
        | :? JsonArray as value -> Some value.Count
        | _ -> None

    let rec private canonicalNode (node: JsonNode) =
        if isNull node then "null"
        else
            match node with
            | :? JsonObject as value ->
                value
                |> Seq.map (fun property ->
                    JsonSerializer.Serialize(property.Key) + ":" + canonicalNode property.Value)
                |> Seq.sort
                |> String.concat ","
                |> fun body -> "{" + body + "}"
            | :? JsonArray as value ->
                value
                |> Seq.map canonicalNode
                |> String.concat ","
                |> fun body -> "[" + body + "]"
            | _ -> node.ToJsonString()

    let private parseReturnTo (baseUri: Uri) (value: string) =
        let mutable parsed = Unchecked.defaultof<Uri>
        if Uri.TryCreate(value, UriKind.Absolute, &parsed) then
            Some(parsed.PathAndQuery, parsed.Scheme.Equals(baseUri.Scheme, StringComparison.OrdinalIgnoreCase)
                 && parsed.Authority.Equals(baseUri.Authority, StringComparison.OrdinalIgnoreCase))
        else None

    let private parseExpires (attributes: Map<string, string>) =
        match Map.tryFind "expires" attributes with
        | None -> None
        | Some value ->
            let style = DateTimeStyles.AssumeUniversal ||| DateTimeStyles.AdjustToUniversal
            let mutable parsed = DateTimeOffset.MinValue
            let rfcValid =
                DateTimeOffset.TryParseExact(value, "ddd, dd MMM yyyy HH:mm:ss 'GMT'",
                    CultureInfo.InvariantCulture, style, &parsed)
            let weekday =
                let comma = value.IndexOf(',')
                if comma <= 0 then "" else value.Substring(0, comma).Trim()
            if not rfcValid then
                // Keep the calendar instant even when a wrong weekday makes the
                // wire value invalid RFC 1123. This lets the receipt distinguish
                // a bad weekday from an incorrect expiry time without recording
                // any cookie value.
                let tail = if value.Length > 5 then value.Substring(5) else ""
                if not (DateTimeOffset.TryParseExact(tail, "dd MMM yyyy HH:mm:ss 'GMT'",
                        CultureInfo.InvariantCulture, style, &parsed)) then
                    fail "malformed Set-Cookie Expires attribute"
            let calendarWeekday = parsed.ToString("ddd", CultureInfo.InvariantCulture)
            Some(parsed, rfcValid, weekday, calendarWeekday)

    let private parseCookieHeader (header: string) =
        let parts = header.Split(';', StringSplitOptions.RemoveEmptyEntries ||| StringSplitOptions.TrimEntries)
        if parts.Length = 0 then fail "empty Set-Cookie header"
        let separator = parts[0].IndexOf('=')
        if separator <= 0 then fail "malformed Set-Cookie name/value"
        let name = parts[0].Substring(0, separator).Trim()
        let wireValue = parts[0].Substring(separator + 1).Trim()
        let mutable attributes = Map.empty
        for part in parts |> Array.skip 1 do
            let equals = part.IndexOf('=')
            let key, value =
                if equals < 0 then part.Trim().ToLowerInvariant(), "true"
                else part.Substring(0, equals).Trim().ToLowerInvariant(), part.Substring(equals + 1).Trim()
            if key = "" || Map.containsKey key attributes then fail "duplicate Set-Cookie attribute"
            attributes <- Map.add key value attributes
        let rawValue =
            match RackCookieEncoding.unescape wireValue with
            | Ok value -> value
            | Error _ -> fail "Set-Cookie value could not be decoded"
        name, wireValue, rawValue, attributes

    let private createCookie (service: CookieService) (now: DateTimeOffset) (baseUri: Uri) (header: string) : Cookie =
        let name, wireValue, value, attributes = parseCookieHeader header
        let expiry = parseExpires attributes
        let mutable sessionKeys = []
        let mutable sessionCanonical = None
        let mutable returnToPath = None
        let mutable returnToOriginMatches = None
        let mutable flashAlert = None
        let mutable flashDiscardCount = None
        let mutable lastRoom = None
        let mutable verifiedToken = None
        match name with
        | "_campfire_session" ->
            match service.DecryptRailsSession(value, now) with
            | None -> fail "encrypted session cookie did not decrypt"
            | Some session ->
                sessionKeys <- session |> Seq.map (fun property -> property.Key) |> Seq.sort |> Seq.toList
                let returnTo = getString session["return_to_after_authenticating"]
                match returnTo with
                | Some target ->
                    match parseReturnTo baseUri target with
                    | Some(path, originMatches) ->
                        returnToPath <- Some path
                        returnToOriginMatches <- Some originMatches
                    | None -> fail "remembered return URL was not absolute"
                | None -> ()
                let flash = session["flash"]
                let flashes = getObjectValue flash "flashes"
                flashAlert <- getString (getObjectValue flashes "alert")
                flashDiscardCount <- getArrayCount (getObjectValue flash "discard")
                let canonicalProperties =
                    session
                    |> Seq.map (fun property ->
                        let jsonValue =
                            if property.Key = "return_to_after_authenticating" then
                                match returnToPath with
                                | Some path -> JsonSerializer.Serialize(path)
                                | None -> canonicalNode property.Value
                            else canonicalNode property.Value
                        JsonSerializer.Serialize(property.Key) + ":" + jsonValue)
                    |> Seq.sort
                    |> String.concat ","
                sessionCanonical <- Some("{" + canonicalProperties + "}")
        | "session_token" ->
            match service.VerifySessionToken(value, now) with
            | Some token -> verifiedToken <- Some token
            | None -> fail "signed session-token cookie did not verify"
        | "last_room" -> lastRoom <- Some value
        | _ -> fail "unexpected Set-Cookie name"
        { Name = name
          WireValue = wireValue
          Value = value
          Attributes = attributes
          ExpiresHeader = Map.tryFind "expires" attributes
          ExpiresUtc = expiry |> Option.map (fun (instant, _, _, _) -> instant)
          ExpiresRfcValid = expiry |> Option.map (fun (_, valid, _, _) -> valid)
          ExpiresWeekday = expiry |> Option.map (fun (_, _, weekday, _) -> weekday)
          ExpiresCalendarWeekday = expiry |> Option.map (fun (_, _, _, weekday) -> weekday)
          SessionKeys = sessionKeys
          SessionCanonical = sessionCanonical
          ReturnToPath = returnToPath
          ReturnToOriginMatches = returnToOriginMatches
          FlashAlert = flashAlert
          FlashDiscardCount = flashDiscardCount
          LastRoom = lastRoom
          VerifiedSessionToken = verifiedToken }

    let private makeClient () =
        let handler = new SocketsHttpHandler(
            AllowAutoRedirect = false,
            UseCookies = false,
            UseProxy = false)
        let client = new HttpClient(handler, true)
        client.Timeout <- TimeSpan.FromSeconds(15.0)
        client

    let private send (client: HttpClient) (hostName: string) (baseUri: Uri) (spec: CaseSpec)
            (userAgent: string) (service: CookieService) (now: DateTimeOffset) : Observation =
        try
            use request = new HttpRequestMessage(new HttpMethod(spec.Method), Uri(baseUri, spec.Path))
            request.Version <- Version(1, 1)
            request.VersionPolicy <- HttpVersionPolicy.RequestVersionExact
            request.Headers.UserAgent.ParseAdd(userAgent)
            match spec.CookieHeader with
            | Some cookie -> request.Headers.TryAddWithoutValidation("Cookie", cookie) |> ignore
            | None -> ()
            use response = client.SendAsync(request, HttpCompletionOption.ResponseContentRead).GetAwaiter().GetResult()
            let body = response.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult()
            let contentType =
                match response.Content.Headers.ContentType with
                | null -> None
                | value -> Some(value.ToString())
            let contentLength =
                match getValues (response.Content.Headers :> HttpHeaders) "Content-Length" with
                | value :: _ ->
                    let mutable parsed = 0L
                    if Int64.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, &parsed) then Some parsed
                    else fail "invalid Content-Length response header"
                | [] -> None
            let cookies =
                getValues (response.Headers :> HttpHeaders) "Set-Cookie"
                |> List.map (createCookie service now baseUri)
            { Host = hostName
              CaseName = spec.Name
              Status = int response.StatusCode
              ContentType = contentType
              Location = getValues (response.Headers :> HttpHeaders) "Location" |> List.tryHead
              BodyBytes = body.Length
              ContentLength = contentLength
              CacheControl =
                match response.Headers.CacheControl with
                | null -> None
                | value -> Some(value.ToString())
              ContentTypeOptions = getValues (response.Headers :> HttpHeaders) "X-Content-Type-Options" |> List.tryHead
              Cookies = cookies }
        with
        | CheckFailure _ -> reraise ()
        | _ -> fail (hostName + "." + spec.Name + " request failed; details suppressed")

    let private normalizeContentType (value: string option) =
        value
        |> Option.map (fun contentType ->
            contentType.Split(';', StringSplitOptions.TrimEntries)
            |> Array.map (fun part -> part.ToLowerInvariant())
            |> String.concat ";")

    let private cookieNames (cookies: Cookie list) = cookies |> List.map (fun cookie -> cookie.Name) |> List.sort

    let private compareExpires (label: string) (left: Cookie) (right: Cookie) =
        match left.ExpiresUtc, right.ExpiresUtc with
        | None, None -> check (label + " expiry absence") true
        | Some a, Some b ->
            check (label + " expiry clock delta") (abs((a - b).TotalSeconds) <= 120.0)
        | _ -> check (label + " expiry presence") false

    let private compareCookieAttributes (label: string) (left: Cookie) (right: Cookie) =
        let leftKeys = left.Attributes |> Map.toSeq |> Seq.map fst |> Set.ofSeq
        let rightKeys = right.Attributes |> Map.toSeq |> Seq.map fst |> Set.ofSeq
        check (label + " attribute names") (leftKeys = rightKeys)
        for key in leftKeys do
            if key <> "expires" then
                let leftValue = left.Attributes[key].ToLowerInvariant()
                let rightValue = right.Attributes[key].ToLowerInvariant()
                check (label + " attribute " + key) (leftValue = rightValue)
        compareExpires label left right

    let private findCookie (label: string) (cookies: Cookie list) (name: string) : Cookie =
        match cookies |> List.filter (fun cookie -> cookie.Name = name) with
        | [ cookie ] -> cookie
        | _ -> fail (label + " expected exactly one " + name + " cookie")

    let private compareCookies (label: string) (left: Cookie list) (right: Cookie list) =
        check (label + " cookie names") (cookieNames left = cookieNames right)
        for name in cookieNames left do
            let a = findCookie label left name
            let b = findCookie label right name
            compareCookieAttributes (label + "." + name) a b
            match name with
            | "_campfire_session" ->
                check (label + " decrypted session state") (a.SessionCanonical = b.SessionCanonical)
            | "session_token" ->
                check (label + " verified token equality") (a.VerifiedSessionToken = b.VerifiedSessionToken)
            | "last_room" -> check (label + " last-room value") (a.LastRoom = b.LastRoom)
            | _ -> fail (label + " unexpected cookie in parity comparison")

    let private checkExpectedAttributes (label: string) (cookie: Cookie) =
        let attr name = Map.tryFind name cookie.Attributes
        check (label + " Path=/") (attr "path" = Some "/")
        check (label + " SameSite=Lax") (attr "samesite" |> Option.map (fun value -> value.ToLowerInvariant()) = Some "lax")
        let expectedHttpOnly = cookie.Name = "_campfire_session" || cookie.Name = "session_token"
        check (label + " HttpOnly") (Map.containsKey "httponly" cookie.Attributes = expectedHttpOnly)
        check (label + " Secure") (Map.containsKey "secure" cookie.Attributes = false)
        check (label + " no Max-Age") (not (Map.containsKey "max-age" cookie.Attributes))
        let expectedPermanent = cookie.Name = "session_token" || cookie.Name = "last_room"
        check (label + " permanent expiry") (cookie.ExpiresUtc.IsSome = expectedPermanent)

    let private checkObservation (spec: CaseSpec) (host: string) (observation: Observation) =
        let label = host + "." + spec.Name
        check (label + " status") (observation.Status = spec.ExpectedStatus)
        check (label + " location") (observation.Location = spec.ExpectedLocation)
        check (label + " content type") (normalizeContentType observation.ContentType = Some "text/html;charset=utf-8")
        check (label + " no-store") (observation.CacheControl |> Option.exists (fun value -> value.ToLowerInvariant().Contains("no-store")))
        check (label + " nosniff") (observation.ContentTypeOptions |> Option.map (fun value -> value.ToLowerInvariant()) = Some "nosniff")
        match spec.ExpectedBodyBytes with
        | Some expected -> check (label + " body bytes") (observation.BodyBytes = expected)
        | None -> ()
        check (label + " cookie names") (cookieNames observation.Cookies = spec.ExpectedCookieNames)
        check (label + " unique cookie names") (cookieNames observation.Cookies |> List.distinct |> List.length = observation.Cookies.Length)
        for cookie in observation.Cookies do checkExpectedAttributes (label + "." + cookie.Name) cookie

    let private checkPair (spec: CaseSpec) (native: Observation) (control: Observation) =
        let label = spec.Name
        check (label + " status parity") (native.Status = control.Status)
        check (label + " content type parity") (normalizeContentType native.ContentType = normalizeContentType control.ContentType)
        check (label + " location parity") (native.Location = control.Location)
        compareCookies label native.Cookies control.Cookies

    let private checkSessionCookie (label: string) (expectedPath: string) (cookie: Cookie) =
        check (label + " return-to path") (cookie.ReturnToPath = Some expectedPath)
        check (label + " return-to origin") (cookie.ReturnToOriginMatches = Some true)

    let private checkFlashCookie (label: string) (cookie: Cookie) =
        check (label + " flash alert") (cookie.FlashAlert = Some "Room not found or inaccessible")
        check (label + " empty flash discard list") (cookie.FlashDiscardCount = Some 0)
        check (label + " flash session keys") (cookie.SessionKeys = [ "flash" ])

    let private checkLastRoomCookie (label: string) (cookie: Cookie) =
        check (label + " last_room value") (cookie.LastRoom = Some "104")

    let private addString (target: JsonObject) (key: string) (value: string) =
        target[key] <- (JsonValue.Create(value) :> JsonNode)

    let private addInt (target: JsonObject) (key: string) (value: int) =
        target[key] <- (JsonValue.Create(value) :> JsonNode)

    let private addInt64 (target: JsonObject) (key: string) (value: int64) =
        target[key] <- (JsonValue.Create(value) :> JsonNode)

    let private addBool (target: JsonObject) (key: string) (value: bool) =
        target[key] <- (JsonValue.Create(value) :> JsonNode)

    let private addOptionalString (target: JsonObject) (key: string) (value: string option) =
        match value with
        | Some text -> addString target key text
        | None -> target[key] <- null

    let private cookieReceipt (cookie: Cookie) : JsonObject =
        let result = JsonObject()
        addString result "name" cookie.Name
        addOptionalString result "path" (Map.tryFind "path" cookie.Attributes)
        addOptionalString result "sameSite" (Map.tryFind "samesite" cookie.Attributes)
        addBool result "httpOnly" (Map.containsKey "httponly" cookie.Attributes)
        addBool result "secure" (Map.containsKey "secure" cookie.Attributes)
        addOptionalString result "maxAge" (Map.tryFind "max-age" cookie.Attributes)
        addOptionalString result "expiresHeader" cookie.ExpiresHeader
        addOptionalString result "expiresUtc" (cookie.ExpiresUtc |> Option.map (fun value -> value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)))
        match cookie.ExpiresRfcValid with
        | Some value -> addBool result "expiresRfcValid" value
        | None -> ()
        addOptionalString result "expiresWeekday" cookie.ExpiresWeekday
        addOptionalString result "expiresCalendarWeekday" cookie.ExpiresCalendarWeekday
        if cookie.Name = "_campfire_session" then
            result["sessionKeys"] <- JsonArray(cookie.SessionKeys |> List.map (fun key -> JsonValue.Create(key) :> JsonNode) |> List.toArray)
            addOptionalString result "returnToPath" cookie.ReturnToPath
            addOptionalString result "flashAlert" cookie.FlashAlert
            match cookie.FlashDiscardCount with
            | Some count -> addInt result "flashDiscardCount" count
            | None -> ()
        elif cookie.Name = "last_room" then addOptionalString result "lastRoom" cookie.LastRoom
        elif cookie.Name = "session_token" then addBool result "verified" cookie.VerifiedSessionToken.IsSome
        result

    let private observationReceipt (observation: Observation) : JsonObject =
        let result = JsonObject()
        addString result "host" observation.Host
        addString result "case" observation.CaseName
        addInt result "status" observation.Status
        addOptionalString result "contentType" observation.ContentType
        addOptionalString result "location" observation.Location
        addInt result "bodyBytes" observation.BodyBytes
        match observation.ContentLength with
        | Some value -> addInt64 result "contentLength" value
        | None -> result["contentLength"] <- null
        addOptionalString result "cacheControl" observation.CacheControl
        addOptionalString result "contentTypeOptions" observation.ContentTypeOptions
        result["cookies"] <- JsonArray(observation.Cookies |> List.map cookieReceipt |> List.map (fun item -> item :> JsonNode) |> List.toArray)
        result

    let private run (args: string array) =
        if args.Length <> 3 then fail "expected private request path, synthetic secret path, and receipt path"
        let requestPath = Path.GetFullPath(args[0])
        let secretPath = Path.GetFullPath(args[1])
        let receiptPath = Path.GetFullPath(args[2])
        let requestConfig = JsonDocument.Parse(File.ReadAllText(requestPath))
        let uriText = requestConfig.RootElement.GetProperty("Uri").GetString()
        let authCookie = requestConfig.RootElement.GetProperty("Cookie").GetString()
        let userAgent = requestConfig.RootElement.GetProperty("UserAgent").GetString()
        let inputUri = Uri(uriText, UriKind.Absolute)
        let relativePath = inputUri.PathAndQuery
        let secretBytes = File.ReadAllBytes(secretPath)
        let secret = Encoding.UTF8.GetString(secretBytes)
        CryptographicOperations.ZeroMemory(secretBytes)
        let service =
            match CookieService.Create(secret) with
            | Ok value -> value
            | Error _ -> fail "synthetic cookie service initialization failed"
        use service = service
        let now = DateTimeOffset.UtcNow
        let nativeBase = Uri("http://127.0.0.1:13773/")
        let controlBase = Uri("http://127.0.0.1:13774/")
        use nativeClient = makeClient ()
        use controlClient = makeClient ()
        let cases =
            [ { Name = "unauth-room104-get"; Method = "GET"; Path = relativePath; CookieHeader = None
                ExpectedStatus = 302; ExpectedLocation = Some "/session/new"; ExpectedCookieNames = [ "_campfire_session" ]; ExpectedBodyBytes = Some 0 }
              { Name = "missing-room-alert-get"; Method = "GET"; Path = "/rooms/999999"; CookieHeader = Some authCookie
                ExpectedStatus = 302; ExpectedLocation = Some "/"; ExpectedCookieNames = [ "_campfire_session" ]; ExpectedBodyBytes = Some 0 }
              { Name = "nonmember-room-alert-get"; Method = "GET"; Path = "/rooms/102"; CookieHeader = Some authCookie
                ExpectedStatus = 302; ExpectedLocation = Some "/"; ExpectedCookieNames = [ "_campfire_session" ]; ExpectedBodyBytes = Some 0 }
              { Name = "room104-get"; Method = "GET"; Path = relativePath; CookieHeader = Some authCookie
                ExpectedStatus = 200; ExpectedLocation = None; ExpectedCookieNames = [ "last_room" ]; ExpectedBodyBytes = None }
              { Name = "room104-head"; Method = "HEAD"; Path = relativePath; CookieHeader = Some authCookie
                ExpectedStatus = 200; ExpectedLocation = None; ExpectedCookieNames = [ "last_room" ]; ExpectedBodyBytes = Some 0 }
              { Name = "unauth-room104-head"; Method = "HEAD"; Path = relativePath; CookieHeader = None
                ExpectedStatus = 302; ExpectedLocation = Some "/session/new"; ExpectedCookieNames = [ "_campfire_session" ]; ExpectedBodyBytes = Some 0 } ]
        let observations = ResizeArray<Observation * Observation>()
        for spec in cases do
            let native = send nativeClient "native" nativeBase spec userAgent service now
            let control = send controlClient "control" controlBase spec userAgent service now
            checkObservation spec "native" native
            checkObservation spec "control" control
            checkPair spec native control
            observations.Add(native, control)

        let unauthNative, unauthControl = observations[0]
        for label, baseUri, observation in
            [ "native unauthenticated" , nativeBase, unauthNative
              "control unauthenticated", controlBase, unauthControl ] do
            let cookie = findCookie label observation.Cookies "_campfire_session"
            checkSessionCookie (label + " GET") relativePath cookie
            check (label + " remembers only return target") (cookie.SessionKeys = [ "return_to_after_authenticating" ])

        let missingNative, missingControl = observations[1]
        let nonmemberNative, nonmemberControl = observations[2]
        for label, observation in
            [ "native missing room", missingNative; "control missing room", missingControl
              "native nonmember room", nonmemberNative; "control nonmember room", nonmemberControl ] do
            checkFlashCookie label (findCookie label observation.Cookies "_campfire_session")

        let getNative, getControl = observations[3]
        let headNative, headControl = observations[4]
        for label, getObservation, headObservation in
            [ "native room", getNative, headNative; "control room", getControl, headControl ] do
            check (label + " GET body present") (getObservation.BodyBytes > 0)
            check (label + " HEAD body empty") (headObservation.BodyBytes = 0)
            check (label + " GET/HEAD status") (getObservation.Status = headObservation.Status)
            check (label + " GET/HEAD type") (normalizeContentType getObservation.ContentType = normalizeContentType headObservation.ContentType)
            compareCookies (label + " GET/HEAD") getObservation.Cookies headObservation.Cookies
            checkLastRoomCookie (label + " GET") (findCookie label getObservation.Cookies "last_room")
            checkLastRoomCookie (label + " HEAD") (findCookie label headObservation.Cookies "last_room")
        let nativeLengthMatches = headNative.ContentLength = Some(int64 getNative.BodyBytes)
        check "native HEAD advertises GET-equivalent Content-Length" nativeLengthMatches
        let controlLengthMatches = headControl.ContentLength = Some(int64 getControl.BodyBytes)

        let unauthHeadNative, unauthHeadControl = observations[5]
        for label, getObservation, headObservation in
            [ "native auth redirect", unauthNative, unauthHeadNative
              "control auth redirect", unauthControl, unauthHeadControl ] do
            check (label + " HEAD body empty") (headObservation.BodyBytes = 0)
            check (label + " GET/HEAD status") (getObservation.Status = headObservation.Status)
            check (label + " GET/HEAD location") (getObservation.Location = headObservation.Location)
            compareCookies (label + " GET/HEAD") getObservation.Cookies headObservation.Cookies
            checkSessionCookie (label + " HEAD") relativePath
                (findCookie label headObservation.Cookies "_campfire_session")

        let root = JsonObject()
        let invalidExpiryCookies =
            observations
            |> Seq.collect (fun (native, control) -> Seq.append native.Cookies control.Cookies)
            |> Seq.filter (fun cookie -> cookie.ExpiresRfcValid = Some false)
            |> Seq.map (fun cookie -> cookie.Name)
            |> Seq.toList
        addString root "result" (if invalidExpiryCookies.IsEmpty then "pass" else "fail")
        addInt root "requestCount" (cases.Length * 2)
        addInt root "assertions" assertionCount
        addString root "nativeEndpoint" "127.0.0.1:13773"
        addString root "controlEndpoint" "127.0.0.1:13774"
        let fixtureState = JsonObject()
        addString fixtureState "unauthenticatedReturnToPath" relativePath
        addString fixtureState "missingRoomFlashAlert" "Room not found or inaccessible"
        addString fixtureState "nonmemberRoomFlashAlert" "Room not found or inaccessible"
        addString fixtureState "validRoomLastRoom" "104"
        root["expectedPlaintextFixtureState"] <- fixtureState
        let parity = JsonObject()
        addBool parity "nativeHeadContentLengthMatchesGet" nativeLengthMatches
        addBool parity "controlHeadContentLengthMatchesGet" controlLengthMatches
        addBool parity "controlHeadFramingDiffersFromNative" (controlLengthMatches <> nativeLengthMatches)
        root["headFraming"] <- parity
        let expiryFailures = JsonArray()
        for native, control in observations do
            for observation in [ native; control ] do
                for cookie in observation.Cookies do
                    match cookie.ExpiresUtc, cookie.ExpiresRfcValid, cookie.ExpiresWeekday, cookie.ExpiresCalendarWeekday with
                    | Some instant, Some valid, Some wireDay, Some calendarDay when not valid ->
                        let item = JsonObject()
                        addString item "host" observation.Host
                        addString item "case" observation.CaseName
                        addString item "cookieName" cookie.Name
                        addString item "expiresUtc" (instant.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture))
                        addString item "wireWeekday" wireDay
                        addString item "calendarWeekday" calendarDay
                        expiryFailures.Add(item)
                    | _ -> ()
        root["invalidExpiresWeekdays"] <- expiryFailures
        let rows = JsonArray()
        for native, control in observations do
            let pair = JsonObject()
            pair["native"] <- observationReceipt native
            pair["control"] <- observationReceipt control
            rows.Add(pair)
        root["cases"] <- rows
        let output = root.ToJsonString(JsonSerializerOptions(WriteIndented = true))
        Directory.CreateDirectory(Path.GetDirectoryName(receiptPath)) |> ignore
        File.WriteAllText(receiptPath, output + Environment.NewLine, UTF8Encoding(false))
        printfn "%s native HTTP cookie parity: cases=%d requests=%d assertions=%d" (if invalidExpiryCookies.IsEmpty then "PASS" else "FAIL") cases.Length (cases.Length * 2) assertionCount
        printfn "HEAD Content-Length: native=%s control=%s"
            (if nativeLengthMatches then "GET-equivalent" else "different")
            (if controlLengthMatches then "GET-equivalent" else "different")
        printfn "Redacted receipt: %s" receiptPath
        if not invalidExpiryCookies.IsEmpty then fail "Set-Cookie Expires weekday did not match its calendar date; see redacted receipt"

    [<EntryPoint>]
    let main args =
        try
            run args
            0
        with
        | CheckFailure label ->
            eprintfn "FAIL %s" label
            1
        | _ ->
            eprintfn "FAIL checker/runtime error; details suppressed"
            1
