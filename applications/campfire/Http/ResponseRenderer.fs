namespace AgentLang.Campfire.Http

open AgentLang

type RenderedResponse =
    { Status: int
      ContentType: string
      Body: string
      Location: string option
      RefreshToken: string option
      RememberReturnTo: bool
      LastRoom: int64 option
      Alert: string option
      CompleteLogin: bool
      CompleteLogout: bool
      /// A CSRF secret Flow generated for a session without one (Rails
      /// real_csrf_token); the host stores it as the session's _csrf_token.
      NewCsrfToken: string option }

/// Checks the closed response contract. Every HTML body is rendered by the
/// Flow application (applications/campfire/views.flow); this host sends it as-is.
module ResponseRenderer =
    let render (value: Value) : Result<RenderedResponse, string> =
        let fail () = invalidOp "Invalid closed Campfire HTTP response."
        let record name = function
            | RecordValue(actual, fields) when actual = name -> fields
            | _ -> fail ()
        let get name fields = Map.tryFind name fields |> Option.defaultWith fail
        let stringValue = function StringValue text -> text | _ -> fail ()
        let namedInt name = function NamedValue(actual, IntValue number) when actual = name -> number | _ -> fail ()
        let optional typ convert = function
            | OptionValue(actual, value) when actual = typ -> Option.map convert value
            | _ -> fail ()
        let boolValue = function BoolValue flag -> flag | _ -> fail ()
        try
            let fields = record "CampfireHttpResponse" value
            if fields.Count <> 10 then fail ()
            // The Flow application renders every HTML body (views.flow); this
            // host only checks the response contract and sends the bytes.
            let renderedBody = get "body" fields |> stringValue
            let status = match get "status" fields with IntValue status when status >= 100L && status <= 599L -> int status | _ -> fail ()
            let view = match get "view" fields with EnumValue("HttpView", caseName) -> caseName | _ -> fail ()
            let text = get "text" fields |> stringValue
            let room = get "room" fields |> optional (TNamed "RoomRow") (record "RoomRow")
            let message = get "message" fields |> optional (TNamed "MessageRow") id
            let token =
                get "refresh-session-token" fields
                |> optional (TNamed "SessionToken") (function NamedValue("SessionToken", StringValue token) -> token | _ -> fail ())
            let remember = get "remember-return-to" fields |> boolValue
            let lastRoom = get "last-room" fields |> optional (TNamed "RoomId") (namedInt "RoomId")
            let newCsrfToken = get "new-csrf-token" fields |> optional TString stringValue
            if newCsrfToken.IsSome && view <> "room" then fail ()
            let noRouteData = room.IsNone && message.IsNone
            let contentType, body, location, alert =
                match view with
                | "empty" when noRouteData && text = "" && not remember && lastRoom.IsNone ->
                    (if renderedBody <> "" then fail ()); "text/plain; charset=utf-8", "", None, None
                | "room-not-found" when status = 200 && noRouteData && not remember && lastRoom.IsNone ->
                    "text/html; charset=utf-8", renderedBody, None, None
                | "login-page" when (status = 200 || status = 401 || status = 429) && noRouteData && not remember && token.IsNone && lastRoom.IsNone ->
                    "text/html; charset=utf-8", renderedBody, None, None
                | "redirect-root" when status = 302 && noRouteData && not remember && lastRoom.IsNone ->
                    (if renderedBody <> "" then fail ()); "text/html; charset=utf-8", "", Some "/", Some text
                | "redirect-session" when status = 302 && noRouteData && text = "" && remember && token.IsNone && lastRoom.IsNone ->
                    (if renderedBody <> "" then fail ()); "text/html; charset=utf-8", "", Some "/session/new", None
                | "redirect-authenticated" when status = 302 && noRouteData && text = "" && not remember && token.IsSome && lastRoom.IsNone ->
                    (if renderedBody <> "" then fail ()); "text/html; charset=utf-8", "", None, None
                | "redirect-first-run" when status = 302 && noRouteData && text = "" && not remember && token.IsNone && lastRoom.IsNone ->
                    (if renderedBody <> "" then fail ()); "text/html; charset=utf-8", "", Some "/first_run", None
                | "redirect-signed-out" when status = 302 && noRouteData && text = "" && not remember && token.IsNone && lastRoom.IsNone ->
                    (if renderedBody <> "" then fail ()); "text/html; charset=utf-8", "", Some "/", None
                | "incompatible-browser" when status = 200 && noRouteData && not remember && lastRoom.IsNone && text = "" ->
                    "text/html; charset=utf-8", renderedBody, None, None
                | "room" when status = 200 && room.IsSome && message.IsNone && not remember && text = "" ->
                    let roomFields = room.Value
                    let roomId = get "id" roomFields |> namedInt "RoomId"
                    if lastRoom |> Option.exists ((<>) roomId) then fail ()
                    "text/html; charset=utf-8", renderedBody, None, None
                | "append-message" when status = 200 && room.IsSome && message.IsSome && not remember && lastRoom.IsNone && text = "" ->
                    let roomId = get "id" room.Value |> namedInt "RoomId"
                    let row = message.Value
                    if (get "room-id" (record "MessageRow" row) |> namedInt "RoomId") <> roomId then fail ()
                    "text/vnd.turbo-stream.html; charset=utf-8",
                    renderedBody, None, None
                | _ -> fail ()
            Ok { Status = status; ContentType = contentType; Body = body; Location = location
                 RefreshToken = token; RememberReturnTo = remember; LastRoom = lastRoom; Alert = alert
                 CompleteLogin = view = "redirect-authenticated"
                 CompleteLogout = view = "redirect-signed-out"
                 NewCsrfToken = newCsrfToken }
        with _ -> Error "HTTP_RESPONSE_CONTRACT_MISMATCH"
