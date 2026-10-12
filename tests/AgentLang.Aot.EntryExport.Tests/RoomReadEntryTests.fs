namespace AgentLang.Aot.EntryExport

open System
open System.Collections.Generic
open System.IO
open System.Text
open AgentLang
open AgentLang.Campfire.Authentication.Host
open AgentLang.Campfire.Browser.Host
open AgentLang.Campfire.Host
open AgentLang.Campfire.Http

module RoomReadEntryTests =
    type private SessionState =
        | SessionMissing
        | SessionLookupError
        | SessionFound of refreshDue: bool

    type private RefreshState =
        | RefreshSuccess
        | RefreshError

    type private UserState =
        | UserFound
        | UserMissing
        | UserLookupError

    type private BotState =
        | BotFound
        | BotMissing
        | BotLookupError

    type private RoomState =
        | RoomFound
        | RoomMissing
        | RoomLookupError

    type private MembershipState =
        | MembershipFound
        | MembershipMissing
        | MembershipLookupError

    type private MessagesState =
        | MessagesFound
        | MessagesLookupError

    type private Scenario =
        { Name: string
          Method: string
          Route: string
          Cookie: string option
          BotKey: string option
          LastRoom: string option
          BrowserFamily: string
          BrowserVersion: string option
          BrowserAtLeastMinimum: bool
          Session: SessionState
          Refresh: RefreshState
          User: UserState
          Bot: BotState
          Room: RoomState
          Membership: MembershipState
          Messages: MessagesState
          ExpectedStatus: int
          ExpectedRefreshToken: string option }

    type private ProviderCall =
        { Operation: string
          Arguments: Value list }

    type private Probe =
        { Environment: HostOperationEnvironment
          SetScenario: Scenario -> unit
          Calls: unit -> ProviderCall list }

    let private dbError = EnumValue("DbError", "unavailable")

    let private resultValue okType value =
        ResultValue(okType, TNamed "DbError", value)

    let private resultOk okType value =
        resultValue okType (Ok value)

    let private resultError okType =
        resultValue okType (Error dbError)

    let private optionValue itemType value = OptionValue(itemType, value)

    let private authUser role =
        RecordValue(
            "AuthUser",
            Map.ofList
                [ "id", NamedValue("UserId", IntValue 7L)
                  "name", StringValue(if role = "bot" then "reader-bot" else "room-reader")
                  "role", EnumValue("Role", role)
                  "status", EnumValue("AuthUserStatus", "active") ])

    let private sessionRow lastActiveAt =
        RecordValue(
            "SessionRow",
            Map.ofList
                [ "id", NamedValue("SessionId", IntValue 88L)
                  "user-id", NamedValue("UserId", IntValue 7L)
                  "token", NamedValue("SessionToken", StringValue "verified-session-token")
                  "ip-address", optionValue (TNamed "RemoteIp") (Some(NamedValue("RemoteIp", StringValue "198.51.100.9")))
                  "user-agent", optionValue TString (Some(StringValue "RoomReadParity/1.0"))
                  "last-active-at", NamedValue("UtcInstant", IntValue lastActiveAt)
                  "created-at", NamedValue("UtcInstant", IntValue 0L)
                  "updated-at", NamedValue("UtcInstant", IntValue 0L) ])

    let private roomRow =
        RecordValue(
            "RoomRow",
            Map.ofList
                [ "id", NamedValue("RoomId", IntValue 101L)
                  "name", optionValue TString (Some(StringValue "General"))
                  "kind", EnumValue("RoomKind", "open")
                  "creator-id", NamedValue("UserId", IntValue 7L)
                  "created-at", StringValue "2026-01-01T00:00:00Z"
                  "updated-at", StringValue "2026-01-01T00:00:00Z" ])

    let private messageRows =
        [ RecordValue(
              "MessageRow",
              Map.ofList
                  [ "id", NamedValue("MessageId", IntValue 1001L)
                    "room-id", NamedValue("RoomId", IntValue 101L)
                    "creator-id", NamedValue("UserId", IntValue 7L)
                    "client-message-id", StringValue "read-fixture-1001"
                    "body", optionValue TString (Some(StringValue "the newest room message"))
                    "created-at", StringValue "2026-01-01T00:00:01Z"
                    "updated-at", StringValue "2026-01-01T00:00:01Z" ])
          RecordValue(
              "MessageRow",
              Map.ofList
                  [ "id", NamedValue("MessageId", IntValue 1002L)
                    "room-id", NamedValue("RoomId", IntValue 101L)
                    "creator-id", NamedValue("UserId", IntValue 12L)
                    "client-message-id", StringValue "read-fixture-1002"
                    "body", optionValue TString None
                    "created-at", StringValue "2026-01-01T00:00:02Z"
                    "updated-at", StringValue "2026-01-01T00:00:02Z" ]) ]

    let private baseScenario name =
        { Name = name
          Method = "get"
          Route = "room-show"
          Cookie = Some "signed-valid-cookie"
          BotKey = None
          LastRoom = None
          BrowserFamily = "other"
          BrowserVersion = None
          BrowserAtLeastMinimum = true
          Session = SessionFound false
          Refresh = RefreshSuccess
          User = UserFound
          Bot = BotMissing
          Room = RoomFound
          Membership = MembershipFound
          Messages = MessagesFound
          ExpectedStatus = 200
          ExpectedRefreshToken = None }

    let private makeProbe initialScenario =
        let calls = ResizeArray<ProviderCall>()
        let mutable scenario = initialScenario
        let record operation arguments =
            let (HostOperationId operationName) = operation
            calls.Add { Operation = operationName; Arguments = arguments }

        let invoke operation arguments =
            record operation arguments
            let (HostOperationId name) = operation
            match name, arguments with
            | "campfire.cookies.verify-session-token", [ StringValue signed; NamedValue("UtcInstant", IntValue _) ] ->
                match signed with
                | "signed-valid-cookie" ->
                    Ok [ optionValue (TNamed "SessionToken") (Some(NamedValue("SessionToken", StringValue "verified-session-token"))) ]
                | "signed-invalid-cookie"
                | "signed-expired-cookie" -> Ok [ optionValue (TNamed "SessionToken") None ]
                | _ -> Error HostOperationFailure.InvocationFailed
            | "campfire.sessions.find-by-token", [ NamedValue("SessionToken", StringValue "verified-session-token") ] ->
                match scenario.Session with
                | SessionMissing ->
                    Ok [ resultOk (TOption(TNamed "SessionRow")) (optionValue (TNamed "SessionRow") None) ]
                | SessionLookupError -> Ok [ resultError (TOption(TNamed "SessionRow")) ]
                | SessionFound refreshDue ->
                    let lastActive = if refreshDue then 0L else 1L
                    Ok [ resultOk (TOption(TNamed "SessionRow")) (optionValue (TNamed "SessionRow") (Some(sessionRow lastActive))) ]
            | "campfire.sessions.refresh-before", [ RecordValue("SessionRefresh", _) ] ->
                match scenario.Refresh with
                | RefreshSuccess ->
                    Ok [ resultOk (TNamed "SessionRow") (sessionRow 3_600_000_001L) ]
                | RefreshError -> Ok [ resultError (TNamed "SessionRow") ]
            | "campfire.users.find-by-id", [ NamedValue("UserId", IntValue 7L) ] ->
                match scenario.User with
                | UserFound -> Ok [ resultOk (TOption(TNamed "AuthUser")) (optionValue (TNamed "AuthUser") (Some(authUser "member"))) ]
                | UserMissing -> Ok [ resultOk (TOption(TNamed "AuthUser")) (optionValue (TNamed "AuthUser") None) ]
                | UserLookupError -> Ok [ resultError (TOption(TNamed "AuthUser")) ]
            | "campfire.users.authenticate-bot-key", [ NamedValue("BotKey", StringValue _) ] ->
                match scenario.Bot with
                | BotFound -> Ok [ resultOk (TOption(TNamed "AuthUser")) (optionValue (TNamed "AuthUser") (Some(authUser "bot"))) ]
                | BotMissing -> Ok [ resultOk (TOption(TNamed "AuthUser")) (optionValue (TNamed "AuthUser") None) ]
                | BotLookupError -> Ok [ resultError (TOption(TNamed "AuthUser")) ]
            | "campfire.browser.version-at-least?", [ StringValue _; StringValue _ ] ->
                Ok [ BoolValue scenario.BrowserAtLeastMinimum ]
            | "campfire.rooms.find-for-user", [ NamedValue("UserId", IntValue 7L); NamedValue("RoomId", IntValue 101L) ] ->
                match scenario.Room with
                | RoomFound -> Ok [ resultOk (TOption(TNamed "RoomRow")) (optionValue (TNamed "RoomRow") (Some roomRow)) ]
                | RoomMissing -> Ok [ resultOk (TOption(TNamed "RoomRow")) (optionValue (TNamed "RoomRow") None) ]
                | RoomLookupError -> Ok [ resultError (TOption(TNamed "RoomRow")) ]
            | "campfire.membership.exists?", [ RecordValue("MembershipKey", _) ] ->
                match scenario.Membership with
                | MembershipFound -> Ok [ resultOk TBool (BoolValue true) ]
                | MembershipMissing -> Ok [ resultOk TBool (BoolValue false) ]
                | MembershipLookupError -> Ok [ resultError TBool ]
            | "campfire.messages.last-page", [ NamedValue("RoomId", IntValue 101L) ] ->
                match scenario.Messages with
                | MessagesFound -> Ok [ resultOk (TList(TNamed "MessageRow")) (ListValue(TNamed "MessageRow", messageRows)) ]
                | MessagesLookupError -> Ok [ resultError (TList(TNamed "MessageRow")) ]
            | _ -> Error HostOperationFailure.InvocationFailed

        let manifest =
            HostComposition.manifest
                [ CampfireHost.manifest
                  CampfireAuthenticationHost.manifest
                  CookieHost.manifest
                  BrowserHost.manifest
                  LoginHost.manifest ]
        let provider: HostOperationProvider =
            { OperationIds = manifest |> Map.toSeq |> Seq.map fst |> Set.ofSeq
              Invoke = invoke
              Dispose = ignore }
        let environment: HostOperationEnvironment =
            { Manifest = manifest
              ProductionProvider = provider
              CreateTestProvider = fun () -> Error HostOperationFailure.TestProviderUnavailable }
        { Environment = environment
          SetScenario = fun next -> scenario <- next; calls.Clear()
          Calls = fun () -> calls |> Seq.toList }

    let private requestValue (scenario: Scenario) =
        let optionalString value = optionValue TString (Option.map StringValue value)
        let optionalBotKey value =
            optionValue (TNamed "BotKey") (Option.map (fun text -> NamedValue("BotKey", StringValue text)) value)
        RecordValue(
            "CampfireHttpRequest",
            Map.ofList
                [ "route", EnumValue("HttpRoute", scenario.Route)
                  "method", EnumValue("RequestMethod", scenario.Method)
                  "room-id", NamedValue("RoomId", IntValue 101L)
                  "now", NamedValue("UtcInstant", IntValue 3_600_000_001L)
                  "remote-ip", NamedValue("RemoteIp", StringValue "198.51.100.9")
                  "user-agent", optionalString (Some "RoomReadParity/1.0")
                  "base-url", StringValue "https://campfire.example.test"
                  "request-is-ssl", BoolValue true
                  "force-ssl", BoolValue false
                  "origin", optionalString None
                  "fetch-site", optionalString None
                  "session-token", optionalString scenario.Cookie
                  "bot-key", optionalBotKey scenario.BotKey
                  "message-form", ResultValue(TOption(TNamed "HttpMessageInput"), TNamed "HttpFormError", Ok(optionValue (TNamed "HttpMessageInput") None))
                  "login-form", ResultValue(TOption(TNamed "HttpLoginInput"), TNamed "HttpFormError", Ok(optionValue (TNamed "HttpLoginInput") None))
                  "logout-form", ResultValue(TOption(TNamed "HttpLogoutInput"), TNamed "HttpFormError", Ok(optionValue (TNamed "HttpLogoutInput") None))
                  "accepts-turbo", BoolValue false
                  "accepts-html", BoolValue true
                  "last-room", optionalString scenario.LastRoom
                  "browser", RecordValue("BrowserInfo", Map.ofList
                      [ "family", EnumValue("BrowserFamily", scenario.BrowserFamily)
                        "version", optionalString scenario.BrowserVersion
                        "is-bot", BoolValue false ]) ])

    let private responseFields label = function
        | Ok(RecordValue("CampfireHttpResponse", fields)) -> fields
        | Ok value -> failwith $"{label}: expected CampfireHttpResponse, got {value}"
        | Error error -> failwith $"{label}: entry invocation failed: {error}"

    let private invokeBound (entry: Runtime.FlowEntryPoint) request =
        try
            match entry.Invoke [ request ] with
            | Ok result when result.Console = [] ->
                match result.Values with
                | [ response ] -> Ok response
                | _ -> Error "HTTP_ENTRYPOINT_CONTRACT_MISMATCH"
            | Ok _ -> Error "HTTP_ENTRYPOINT_CONTRACT_MISMATCH"
            | Error diagnostic -> Error diagnostic.Message
        with error -> Error error.Message

    let private invokeApplication (application: CampfireApplication) request =
        try application.Invoke request with error -> Error error.Message

    let private extractReachableHostOperations (program: IrProgram) root =
        let rec callsInOperation = function
            | IrOperation.Call call -> [ call ]
            | IrOperation.ListMap(call, _, _)
            | IrOperation.ListFilter(call, _)
            | IrOperation.ListEach(call, _)
            | IrOperation.ListFold(call, _, _) -> [ call ]
            | IrOperation.Scope block -> callsInBlock block
            | IrOperation.If(thenBlock, elseBlock) -> callsInBlock thenBlock @ callsInBlock elseBlock
            | IrOperation.MatchOption(_, someBlock, noneBlock) -> callsInBlock someBlock @ callsInBlock noneBlock
            | IrOperation.MatchResult(_, _, okBlock, errorBlock) -> callsInBlock okBlock @ callsInBlock errorBlock
            | IrOperation.MatchEnum(_, cases) -> cases |> List.collect (snd >> callsInBlock)
            | IrOperation.MakeRecord(call, _, validator)
            | IrOperation.WrapScalar(call, _, validator) -> call :: Option.toList validator
            | IrOperation.GetRecordField(call, _, _)
            | IrOperation.UnwrapScalar(call, _)
            | IrOperation.MakeEnumCase(call, _, _) -> [ call ]
            | IrOperation.Constant _
            | IrOperation.ListEmpty _
            | IrOperation.ListSingleton _
            | IrOperation.OptionNone _
            | IrOperation.OptionSome _
            | IrOperation.ResultOk _
            | IrOperation.ResultError _
            | IrOperation.StoreLocal _
            | IrOperation.LoadLocal _ -> []
        and callsInBlock (block: IrBlock) =
            block.Code |> List.collect (fun instruction -> callsInOperation instruction.Operation)

        let pending = Queue<WordId>()
        let visited = HashSet<WordId>()
        let hostOperations = HashSet<HostOperationId>()
        pending.Enqueue root
        while pending.Count > 0 do
            let word = pending.Dequeue()
            if visited.Add word then
                match program.FunctionsById.TryFind word with
                | None -> ()
                | Some functionBody ->
                    for call in callsInBlock functionBody.FunctionBody do
                        match call.ResolvedTarget with
                        | HostOperationTarget operation -> hostOperations.Add operation |> ignore
                        | UserWordTarget(target, _) -> pending.Enqueue target
                        | PrimitiveTarget _
                        | GeneratedWordTarget _ -> ()
        hostOperations |> Set.ofSeq

    let private parityCases =
        [ { baseScenario "get-missing-cookie" with Cookie = None; ExpectedStatus = 302 }
          { baseScenario "get-invalid-cookie" with Cookie = Some "signed-invalid-cookie"; ExpectedStatus = 302 }
          { baseScenario "get-expired-cookie" with Cookie = Some "signed-expired-cookie"; ExpectedStatus = 302 }
          { baseScenario "get-valid-cookie-with-missing-session" with Session = SessionMissing; ExpectedStatus = 302 }
          { baseScenario "get-session-lookup-error" with Session = SessionLookupError; ExpectedStatus = 500 }
          { baseScenario "get-refresh-due" with Session = SessionFound true; ExpectedRefreshToken = Some "verified-session-token" }
          { baseScenario "head-session-recent" with Method = "head" }
          { baseScenario "get-refresh-error" with Session = SessionFound true; Refresh = RefreshError; ExpectedStatus = 500 }
          { baseScenario "get-missing-user-after-refresh" with Session = SessionFound true; User = UserMissing; ExpectedStatus = 500; ExpectedRefreshToken = Some "verified-session-token" }
          { baseScenario "get-user-error-after-refresh" with Session = SessionFound true; User = UserLookupError; ExpectedStatus = 500 }
          { baseScenario "get-refresh-then-room-error" with Session = SessionFound true; Room = RoomLookupError; ExpectedStatus = 500; ExpectedRefreshToken = Some "verified-session-token" }
          { baseScenario "get-bot-key-active" with Cookie = None; BotKey = Some "reader-bot-key"; Bot = BotFound; ExpectedStatus = 403 }
          { baseScenario "get-bot-key-missing" with Cookie = Some "signed-invalid-cookie"; BotKey = Some "unknown-bot-key"; Bot = BotMissing; ExpectedStatus = 302 }
          { baseScenario "get-bot-key-error" with Cookie = None; BotKey = Some "reader-bot-key"; Bot = BotLookupError; ExpectedStatus = 500 }
          { baseScenario "get-unsupported-internet-explorer" with BrowserFamily = "internet-explorer"; BrowserVersion = Some "11"; BrowserAtLeastMinimum = false; ExpectedStatus = 200 }
          { baseScenario "get-unsupported-old-chrome" with BrowserFamily = "chrome"; BrowserVersion = Some "119"; BrowserAtLeastMinimum = false; ExpectedStatus = 200 }
          { baseScenario "get-room-unavailable" with Room = RoomLookupError; ExpectedStatus = 500 }
          { baseScenario "get-room-not-found-for-member" with Room = RoomMissing; ExpectedStatus = 302 }
          { baseScenario "get-room-row-without-membership" with Membership = MembershipMissing; ExpectedStatus = 500 }
          { baseScenario "get-membership-error" with Membership = MembershipLookupError; ExpectedStatus = 500 }
          { baseScenario "get-message-page-error" with Messages = MessagesLookupError; ExpectedStatus = 500 }
          { baseScenario "get-populated-messages-and-no-last-room-cookie" with LastRoom = None }
          { baseScenario "get-populated-messages-with-matching-last-room-cookie" with LastRoom = Some "101" }
          { baseScenario "get-populated-messages-with-different-last-room-cookie" with LastRoom = Some "77" } ]

    let private guardCases =
        [ for route in [ "message-create"; "session-new"; "session-create"; "session-destroy" ] do
              yield { baseScenario $"guard-route-{route}" with Route = route; ExpectedStatus = 500 }
          yield { baseScenario "guard-unsafe-room-read-method" with Method = "unsafe"; ExpectedStatus = 500 } ]

    let run runRoot (check: string -> bool -> unit) =
        let mutable assertionCount = 0
        let localCheck label condition =
            assertionCount <- assertionCount + 1
            check label condition

        let repositoryRoot = Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, "../.."))
        let sourceDirectory = Path.Combine(repositoryRoot, "applications", "campfire")
        let parityRoot = Path.Combine(runRoot, "room-read-entry-parity")
        let currentProject = Path.Combine(parityRoot, "current-project")
        let referenceProject = Path.Combine(parityRoot, "reference-project")
        let referenceSource = Path.Combine(parityRoot, "reference-source")
        Directory.CreateDirectory(parityRoot) |> ignore
        Directory.CreateDirectory(referenceSource) |> ignore
        for sourcePath in Directory.EnumerateFiles(sourceDirectory, "*.flow", SearchOption.TopDirectoryOnly) do
            File.Copy(sourcePath, Path.Combine(referenceSource, Path.GetFileName(sourcePath)), true)
        let fixturePath = Path.Combine(__SOURCE_DIRECTORY__, "fixtures", "room-read-reference-http.flow")
        File.Copy(fixturePath, Path.Combine(referenceSource, "http.flow"), true)
        File.WriteAllText(Path.Combine(referenceSource, "http-room-read-tests.flow"), "", UTF8Encoding(false))

        let initial = baseScenario "provider-startup"
        let currentProbe = makeProbe initial
        let referenceProbe = makeProbe initial
        let current = Application.create currentProject sourceDirectory currentProbe.Environment
        let reference = Application.create referenceProject referenceSource referenceProbe.Environment
        let currentEntry =
            current.Engine.BindFlowEntryPoint(
                "campfire.http.room-read",
                [ TNamed "CampfireHttpRequest" ],
                [ TNamed "CampfireHttpResponse" ])
        localCheck "virtual application startup makes no provider calls" (currentProbe.Calls().IsEmpty && referenceProbe.Calls().IsEmpty)

        let exported =
            current.Engine.ExportVerifiedFlowEntryPoint(
                "campfire.http.room-read",
                [ TNamed "CampfireHttpRequest" ],
                [ TNamed "CampfireHttpResponse" ])
        let verified = VerifiedIrBody.program exported
        let program = VerifiedIrProgram.inspect verified
        let rootFunction =
            program.FunctionsById
            |> Map.toSeq
            |> Seq.map snd
            |> Seq.find (fun functionBody -> functionBody.FunctionName = "campfire.http.room-read")
        let rootWord = rootFunction.FunctionId
        let reachable = extractReachableHostOperations program rootWord
        let reachableNames =
            reachable
            |> Seq.map (fun (HostOperationId name) -> name)
            |> Set.ofSeq
        let expectedReachableNames =
            Set.ofList
                [ "campfire.cookies.verify-session-token"
                  "campfire.sessions.find-by-token"
                  "campfire.sessions.refresh-before"
                  "campfire.users.find-by-id"
                  "campfire.users.authenticate-bot-key"
                  "campfire.browser.version-at-least?"
                  "campfire.rooms.find-for-user"
                  "campfire.membership.exists?"
                  "campfire.messages.last-page" ]
        localCheck "fixed room-read entry is compiler-authorized IR" (VerifiedIrProgram.isBackendExecutable verified)
        localCheck "reachable room-read host closure contains the exact nine read operations" (reachableNames = expectedReachableNames)
        localCheck "room-read declared effect set is database read and write only" (rootFunction.FunctionDeclaredEffects = Set.ofList [ IrEffect.DatabaseRead; IrEffect.DatabaseWrite ])
        localCheck "room-read inferred effect set excludes clock and randomness" (rootFunction.FunctionInferredEffects = Set.ofList [ IrEffect.DatabaseRead; IrEffect.DatabaseWrite ])

        for scenario in parityCases do
            currentProbe.SetScenario scenario
            referenceProbe.SetScenario scenario
            let request = requestValue scenario
            let currentResult = invokeBound currentEntry request
            let referenceResult = invokeApplication reference request
            let currentCalls = currentProbe.Calls()
            let referenceCalls = referenceProbe.Calls()
            localCheck $"{scenario.Name}: complete response parity with frozen generic dispatcher" (currentResult = referenceResult)
            localCheck $"{scenario.Name}: provider operation order and exact arguments match" (currentCalls = referenceCalls)
            let fields = responseFields scenario.Name currentResult
            match fields.TryFind "status" with
            | Some(IntValue status) -> localCheck $"{scenario.Name}: independent expected status" (status = int64 scenario.ExpectedStatus)
            | _ -> localCheck $"{scenario.Name}: response status is an integer" false
            let expectedRefresh =
                optionValue (TNamed "SessionToken") (Option.map (fun text -> NamedValue("SessionToken", StringValue text)) scenario.ExpectedRefreshToken)
            localCheck $"{scenario.Name}: independent session-refresh directive" (fields.TryFind "refresh-session-token" = Some expectedRefresh)

        for scenario in guardCases do
            currentProbe.SetScenario scenario
            let response = invokeBound currentEntry (requestValue scenario)
            let fields = responseFields scenario.Name response
            localCheck $"{scenario.Name}: fixed room-read guard returns status 500" (fields.TryFind "status" = Some(IntValue 500L))
            localCheck $"{scenario.Name}: route and method guard runs before every host provider" (currentProbe.Calls().IsEmpty)

        printfn "Campfire room-read entry parity: %d assertions; %d legacy comparisons; %d entry guards; passed=true" assertionCount parityCases.Length guardCases.Length
        assertionCount
