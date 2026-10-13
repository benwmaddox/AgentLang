namespace AgentLang.Campfire.Http

open System.Globalization
open System.Text.Encodings.Web
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
      CompleteLogout: bool }

/// Only fixed templates are executable HTML. Every application string is
/// encoded, including existing rich-text rows; Action Text parity is later work.
module ResponseRenderer =
    let private encode (text: string) = HtmlEncoder.Default.Encode(text)

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
        let messageHtml value =
            let fields = record "MessageRow" value
            let id = get "id" fields |> namedInt "MessageId"
            let body = get "body" fields |> optional TString stringValue |> Option.defaultValue ""
            let clientId = get "client-message-id" fields |> stringValue
            // Rails-shaped identity: messages_helper#message_tag and dom_id(message, :presentation).
            // Message#to_key is [client_message_id], so DOM ids use it; data-message-id is the row id.
            let idText = id.ToString(CultureInfo.InvariantCulture)
            let domKey = encode clientId
            $"<div id=\"message_{domKey}\" class=\"message\" data-message-id=\"{idText}\"><div id=\"presentation_message_{domKey}\" dir=\"auto\">{encode body}</div></div>"
        try
            let fields = record "CampfireHttpResponse" value
            if fields.Count <> 9 then fail ()
            let status = match get "status" fields with IntValue status when status >= 100L && status <= 599L -> int status | _ -> fail ()
            let view = match get "view" fields with EnumValue("HttpView", caseName) -> caseName | _ -> fail ()
            let text = get "text" fields |> stringValue
            let room = get "room" fields |> optional (TNamed "RoomRow") (record "RoomRow")
            let message = get "message" fields |> optional (TNamed "MessageRow") id
            let messages = match get "messages" fields with ListValue(TNamed "MessageRow", values) -> values | _ -> fail ()
            let token =
                get "refresh-session-token" fields
                |> optional (TNamed "SessionToken") (function NamedValue("SessionToken", StringValue token) -> token | _ -> fail ())
            let remember = get "remember-return-to" fields |> boolValue
            let lastRoom = get "last-room" fields |> optional (TNamed "RoomId") (namedInt "RoomId")
            let noRouteData = room.IsNone && message.IsNone && messages.IsEmpty
            let contentType, body, location, alert =
                match view with
                | "empty" when noRouteData && text = "" && not remember && lastRoom.IsNone ->
                    "text/plain; charset=utf-8", "", None, None
                | "room-not-found" when status = 200 && noRouteData && not remember && lastRoom.IsNone ->
                    "text/html; charset=utf-8", $"<!doctype html><html><body><p>{encode text}</p></body></html>", None, None
                | "login-page" when (status = 200 || status = 401 || status = 429) && noRouteData && not remember && token.IsNone && lastRoom.IsNone ->
                    let alertHtml =
                        if status = 401 || status = 429 then
                            "<p role=\"alert\">Too many requests or unauthorized.</p>"
                        else ""
                    let email = encode text
                    "text/html; charset=utf-8",
                    $"<!doctype html><html><body>{alertHtml}<form method=\"post\" action=\"/session\"><label for=\"email_address\">Email address</label><input id=\"email_address\" name=\"email_address\" type=\"email\" autocomplete=\"username\" value=\"{email}\"><label for=\"password\">Password</label><input id=\"password\" name=\"password\" type=\"password\" autocomplete=\"current-password\" maxlength=\"72\"><button type=\"submit\">Sign in</button></form></body></html>", None, None
                | "redirect-root" when status = 302 && noRouteData && not remember && lastRoom.IsNone ->
                    "text/html; charset=utf-8", "", Some "/", Some text
                | "redirect-session" when status = 302 && noRouteData && text = "" && remember && token.IsNone && lastRoom.IsNone ->
                    "text/html; charset=utf-8", "", Some "/session/new", None
                | "redirect-authenticated" when status = 302 && noRouteData && text = "" && not remember && token.IsSome && lastRoom.IsNone ->
                    "text/html; charset=utf-8", "", None, None
                | "redirect-first-run" when status = 302 && noRouteData && text = "" && not remember && token.IsNone && lastRoom.IsNone ->
                    "text/html; charset=utf-8", "", Some "/first_run", None
                | "redirect-signed-out" when status = 302 && noRouteData && text = "" && not remember && token.IsNone && lastRoom.IsNone ->
                    "text/html; charset=utf-8", "", Some "/", None
                | "incompatible-browser" when status = 200 && noRouteData && not remember && lastRoom.IsNone && text = "" ->
                    "text/html; charset=utf-8", "<!doctype html><html><body><p>Please upgrade your browser.</p></body></html>", None, None
                | "room" when status = 200 && room.IsSome && message.IsNone && not remember && text = "" ->
                    let roomFields = room.Value
                    let roomId = get "id" roomFields |> namedInt "RoomId"
                    if lastRoom |> Option.exists ((<>) roomId) then fail ()
                    let name = get "name" roomFields |> optional TString stringValue |> Option.defaultValue ""
                    let rows = messages |> List.map (fun row ->
                        let rowFields = record "MessageRow" row
                        if (get "room-id" rowFields |> namedInt "RoomId") <> roomId then fail ()
                        messageHtml row) |> String.concat ""
                    let path = $"/rooms/{roomId.ToString(CultureInfo.InvariantCulture)}/messages"
                    "text/html; charset=utf-8",
                    $"<!doctype html><html><body><h1>{encode name}</h1><section id=\"messages\">{rows}</section><form method=\"post\" action=\"{encode path}\"><textarea name=\"message[body]\"></textarea><button type=\"submit\">Send</button></form></body></html>", None, None
                | "append-message" when status = 200 && room.IsSome && message.IsSome && messages.IsEmpty && not remember && lastRoom.IsNone && text = "" ->
                    let roomId = get "id" room.Value |> namedInt "RoomId"
                    let row = message.Value
                    if (get "room-id" (record "MessageRow" row) |> namedInt "RoomId") <> roomId then fail ()
                    "text/vnd.turbo-stream.html; charset=utf-8",
                    $"<turbo-stream action=\"append\" target=\"messages\"><template>{messageHtml row}</template></turbo-stream>", None, None
                | _ -> fail ()
            Ok { Status = status; ContentType = contentType; Body = body; Location = location
                 RefreshToken = token; RememberReturnTo = remember; LastRoom = lastRoom; Alert = alert
                 CompleteLogin = view = "redirect-authenticated"
                 CompleteLogout = view = "redirect-signed-out" }
        with _ -> Error "HTTP_RESPONSE_CONTRACT_MISMATCH"
