namespace AgentLang.Campfire.Http

open System
open System.Globalization
open System.IO
open System.Text
open System.Text.Json.Nodes
open System.Threading.Tasks
open AgentLang
open AgentLang.Campfire.Cookies
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Http
open Microsoft.AspNetCore.Http.Features
open Microsoft.AspNetCore.WebUtilities
open Microsoft.Extensions.Primitives
open Microsoft.Net.Http.Headers

type HttpApplicationOptions =
    { PublicBaseUri: Uri
      ForceSsl: bool
      Cookies: CookieService
      Clock: unit -> DateTimeOffset
      NormalizeBrowser: string option -> Value
      Invoke: Value -> Result<Value, string> }

module Transport =
    [<Literal>]
    let MaximumBodyBytes = 65536L

    let private optional typ convert value = OptionValue(typ, Option.map convert value)
    let private stringOption value = optional TString StringValue value

    let private cookieMap (request: HttpRequest) =
        let raw = request.Headers.Cookie.ToString()
        if Encoding.UTF8.GetByteCount(raw) > 16384 then Error 431
        else
            let mutable values = Map.empty
            let mutable valid = true
            for part in raw.Split(';', StringSplitOptions.RemoveEmptyEntries) do
                let pair = part.Trim().Split('=', 2)
                if pair.Length = 2 && Set.contains pair[0] (Set.ofList [ "session_token"; "_campfire_session"; "last_room" ]) then
                    if Map.containsKey pair[0] values then valid <- false
                    else
                        match RackCookieEncoding.unescape pair[1] with
                        | Ok value -> values <- Map.add pair[0] value values
                        | Error _ -> valid <- false
            if valid then Ok values else Error 400

    let private header name (request: HttpRequest) =
        match request.Headers.TryGetValue name with
        | true, values when values.Count = 1 -> Some(string values[0])
        | true, _ -> Some "invalid-duplicate-header"
        | false, _ -> None

    let private rubyStrip (text: string) = text.Trim([| '\000'; '\009'; '\010'; '\011'; '\012'; '\013'; ' ' |])

    let private acceptsTurbo (request: HttpRequest) =
        let raw = request.Headers.Accept.ToString()
        let compact = raw |> Seq.filter (Char.IsWhiteSpace >> not) |> Seq.toArray |> String
        let xhr = request.Headers["X-Requested-With"].ToString() = "XMLHttpRequest"
        match request.Query.TryGetValue "format" with
        | true, values when values.Count = 1 -> string values[0] = "turbo_stream"
        | true, _ -> false
        | false, _ when String.IsNullOrWhiteSpace raw -> false
        | false, _ when not xhr && (compact.Contains(",*/*", StringComparison.Ordinal) || compact.Contains("*/*,", StringComparison.Ordinal)) -> false
        | false, _ ->
            try
                let parsed = MediaTypeHeaderValue.ParseList(request.Headers.Accept.ToArray())
                parsed
                |> Seq.exists (fun media ->
                    let name = media.MediaType.ToString()
                    // The pinned Rails/Rust list retains q=0 entries. With
                    // only Turbo offered, quality order cannot alter a match.
                    name = "text/vnd.turbo-stream.html" || name = "text/*" || name = "*/*")
            with _ -> false

    let private formValue (request: HttpRequest) = task {
        let error caseName = ResultValue(TOption(TNamed "HttpMessageInput"), TNamed "HttpFormError", Error(EnumValue("HttpFormError", caseName)))
        let ok value = ResultValue(TOption(TNamed "HttpMessageInput"), TNamed "HttpFormError", Ok(OptionValue(TNamed "HttpMessageInput", value)))
        let mediaType = if isNull request.ContentType then "" else request.ContentType.Split(';', 2) |> Array.head |> fun value -> value.Trim()
        if not (mediaType.Equals("application/x-www-form-urlencoded", StringComparison.OrdinalIgnoreCase)) then
            return error "unsupported-media"
        else
            try
                let options = FormOptions(ValueCountLimit = 32, KeyLengthLimit = 128, ValueLengthLimit = 32768)
                request.HttpContext.Features.Set<IFormFeature>(FormFeature(request, options))
                let! form = request.ReadFormAsync(request.HttpContext.RequestAborted)
                let get name =
                    match form.TryGetValue name with
                    | true, values when values.Count = 1 -> Some(string values[0])
                    | _ -> None
                let duplicate = form |> Seq.exists (fun pair -> pair.Value.Count <> 1)
                if duplicate then return error "invalid-form"
                elif form.Keys |> Seq.exists (fun key -> key = "message[attachment]" || key.StartsWith("message[attachment][", StringComparison.Ordinal)) then
                    return error "unsupported-attachment"
                elif form.ContainsKey "message" then return error "invalid-form"
                elif form.Keys |> Seq.exists (fun key -> key.StartsWith("message[", StringComparison.Ordinal)) then
                    return ok (Some(RecordValue("HttpMessageInput", Map.ofList
                        [ "client-message-id", stringOption (get "message[client_message_id]")
                          "body", stringOption (get "message[body]") ])))
                else return ok None
            with
            | :? InvalidDataException -> return error "invalid-form"
            | :? BadHttpRequestException as failure ->
                return error (if failure.StatusCode = 413 then "body-too-large" else "invalid-form")
    }

    let private buildRequest (options: HttpApplicationOptions) (context: HttpContext) route methodName roomId cookies (now: DateTimeOffset) = task {
        let request = context.Request
        let! form =
            if route = "message-create" then formValue request
            else Task.FromResult(ResultValue(TOption(TNamed "HttpMessageInput"), TNamed "HttpFormError", Ok(OptionValue(TNamed "HttpMessageInput", None))))
        let agent = header "User-Agent" request
        let queryKey = request.Query.TryGetValue "bot_key"
        let botKey =
            match queryKey with
            | true, values when values.Count = 1 ->
                let stripped = rubyStrip (string values[0])
                if stripped = "" then None else Some stripped
            | _ -> None
        let ip = context.Connection.RemoteIpAddress
        let remote = if isNull ip then "" elif ip.IsIPv4MappedToIPv6 then ip.MapToIPv4().ToString() else ip.ToString()
        let instant = (now.UtcTicks - DateTimeOffset.UnixEpoch.UtcTicks) / 10L
        return RecordValue("CampfireHttpRequest", Map.ofList
            [ "route", EnumValue("HttpRoute", route)
              "method", EnumValue("RequestMethod", methodName)
              "room-id", NamedValue("RoomId", IntValue roomId)
              "now", NamedValue("UtcInstant", IntValue instant)
              "remote-ip", NamedValue("RemoteIp", StringValue remote)
              "user-agent", stringOption agent
              "base-url", StringValue(options.PublicBaseUri.GetLeftPart(UriPartial.Authority))
              "request-is-ssl", BoolValue(options.PublicBaseUri.Scheme = "https")
              "force-ssl", BoolValue options.ForceSsl
              "origin", stringOption (header "Origin" request)
              "fetch-site", stringOption (header "Sec-Fetch-Site" request)
              "session-token", stringOption (Map.tryFind "session_token" cookies)
              "bot-key", optional (TNamed "BotKey") (fun value -> NamedValue("BotKey", StringValue value)) botKey
              "message-form", form
              "accepts-turbo", BoolValue(acceptsTurbo request)
              "last-room", stringOption (Map.tryFind "last_room" cookies)
              "browser", options.NormalizeBrowser agent ])
    }

    let private finalizeCookies (options: HttpApplicationOptions) (context: HttpContext) cookies (now: DateTimeOffset) (rendered: RenderedResponse) =
        let wire name raw permanent httpOnly =
            match RackCookieEncoding.escape raw with
            | Error _ -> Error "HTTP_COOKIE_LIMIT"
            | Ok escaped ->
                let expires = if permanent then "; Expires=" + now.AddYears(20).UtcDateTime.ToString("R", CultureInfo.InvariantCulture) else ""
                let secure = if options.PublicBaseUri.Scheme = "https" || options.ForceSsl then "; Secure" else ""
                let visibility = if httpOnly then "; HttpOnly" else ""
                Ok($"{name}={escaped}; Path=/; SameSite=Lax{expires}{secure}{visibility}")
        let sessionMutation = rendered.RememberReturnTo || rendered.Alert.IsSome
        let sessionCookie =
            if sessionMutation then
                let state =
                    Map.tryFind "_campfire_session" cookies
                    |> Option.bind (fun raw -> options.Cookies.DecryptRailsSession(raw, now))
                    |> Option.defaultWith (fun () -> JsonObject())
                if rendered.RememberReturnTo then
                    // Authority is fixed configuration, never an arbitrary Host or forwarded header.
                    let target = options.PublicBaseUri.GetLeftPart(UriPartial.Authority) + context.Request.PathBase.ToString() + context.Request.Path.ToString() + context.Request.QueryString.ToString()
                    state["return_to_after_authenticating"] <- JsonValue.Create target
                match rendered.Alert with
                | Some alert ->
                    let flash = JsonObject()
                    flash["discard"] <- JsonArray()
                    let flashes = JsonObject()
                    flashes["alert"] <- JsonValue.Create alert
                    flash["flashes"] <- flashes
                    state["flash"] <- flash
                | None -> ()
                options.Cookies.EncryptRailsSession(state, now)
                |> Result.mapError (fun _ -> "HTTP_COOKIE_LIMIT")
                |> Result.bind (fun raw -> wire "_campfire_session" raw false true)
                |> Result.map Some
            else Ok None
        let tokenCookie =
            match rendered.RefreshToken with
            | None -> Ok None
            | Some token -> options.Cookies.SignSessionToken(token, now)
                            |> Result.mapError (fun _ -> "HTTP_COOKIE_LIMIT")
                            |> Result.bind (fun raw -> wire "session_token" raw true true)
                            |> Result.map Some
        let lastCookie =
            match rendered.LastRoom with
            | None -> Ok None
            | Some room -> wire "last_room" (room.ToString(CultureInfo.InvariantCulture)) true false |> Result.map Some
        match sessionCookie, tokenCookie, lastCookie with
        | Ok session, Ok token, Ok last -> Ok([ session; token; last ] |> List.choose id)
        | _ -> Error "HTTP_COOKIE_LIMIT"

    let private isOversizedRequest = function
        | RecordValue("CampfireHttpRequest", fields) ->
            match Map.tryFind "message-form" fields with
            | Some(ResultValue(_, _, Error(EnumValue("HttpFormError", "body-too-large")))) -> true
            | _ -> false
        | _ -> false

    /// Configured public authority is mandatory. Forwarded headers are ignored
    /// here; deployment behind a trusted proxy must supply its public authority.
    let mapRoutes (app: WebApplication) (options: HttpApplicationOptions) =
        let baseUri = options.PublicBaseUri
        if isNull baseUri || not baseUri.IsAbsoluteUri || (baseUri.Scheme <> "http" && baseUri.Scheme <> "https")
           || baseUri.UserInfo <> "" || baseUri.AbsolutePath <> "/" || baseUri.Query <> "" || baseUri.Fragment <> "" then
            invalidArg "options" "A fixed public HTTP(S) authority is required."
        let gate = obj()
        let handle route methodName (context: HttpContext) = task {
            let request = context.Request
            let hostMatches = request.Host.Value.Equals(baseUri.Authority, StringComparison.OrdinalIgnoreCase)
            let headerBytes = request.Headers |> Seq.sumBy (fun pair -> Encoding.UTF8.GetByteCount(pair.Key) + Encoding.UTF8.GetByteCount(pair.Value.ToString()))
            let mutable roomId = 0L
            let routeId = string context.Request.RouteValues["roomId"]
            if not hostMatches then context.Response.StatusCode <- 400
            elif headerBytes > 32768 then context.Response.StatusCode <- 431
            elif request.ContentLength.HasValue && request.ContentLength.Value > MaximumBodyBytes then context.Response.StatusCode <- 413
            elif not (Int64.TryParse(routeId, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, &roomId)) then context.Response.StatusCode <- 400
            else
                let bodyFeature = context.Features.Get<IHttpMaxRequestBodySizeFeature>()
                if not (isNull bodyFeature) && not bodyFeature.IsReadOnly then bodyFeature.MaxRequestBodySize <- Nullable MaximumBodyBytes
                match cookieMap request with
                | Error status -> context.Response.StatusCode <- status
                | Ok cookies ->
                    let now = options.Clock()
                    let! input = buildRequest options context route methodName roomId cookies now
                    // Unknown-length bodies must have the same transport limit
                    // as Content-Length bodies, before authentication or IO.
                    let result =
                        if isOversizedRequest input then Error "HTTP_BODY_TOO_LARGE"
                        else lock gate (fun () -> options.Invoke input |> Result.bind ResponseRenderer.render)
                    match result with
                    | Error "HTTP_BODY_TOO_LARGE" -> context.Response.StatusCode <- 413
                    | Error _ -> context.Response.StatusCode <- 500
                    | Ok rendered ->
                        match finalizeCookies options context cookies now rendered with
                        | Error _ -> context.Response.StatusCode <- 500
                        | Ok cookieHeaders ->
                            context.Response.StatusCode <- rendered.Status
                            context.Response.ContentType <- rendered.ContentType
                            context.Response.Headers["X-Content-Type-Options"] <- StringValues "nosniff"
                            context.Response.Headers["Cache-Control"] <- StringValues "no-store"
                            match rendered.Location with Some location -> context.Response.Headers.Location <- StringValues location | None -> ()
                            for cookie in cookieHeaders do context.Response.Headers.Append("Set-Cookie", StringValues cookie)
                            if request.Method <> "HEAD" then do! context.Response.WriteAsync(rendered.Body, context.RequestAborted)
        }
        app.MapMethods("/rooms/{roomId}", [| "GET"; "HEAD" |], Func<HttpContext, Task>(fun context ->
            handle "room-show" (if context.Request.Method = "HEAD" then "head" else "get") context :> Task)) |> ignore
        app.MapPost("/rooms/{roomId}/messages", Func<HttpContext, Task>(fun context -> handle "message-create" "unsafe" context :> Task)) |> ignore
