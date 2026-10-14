namespace AgentLang.Campfire.Http

open System
open System.IO
open System.Text.Json.Nodes
open AgentLang
open AgentLang.Campfire.Browser.Host

type CampfireApplication =
    { Engine: Runtime.Engine
      Invoke: Value -> Result<Value, string> }

module Application =
    let capabilities = Set.ofList [ "db.read"; "db.write"; "clock.read"; "random.read" ]

    let normalizeBrowser (userAgent: string option) =
        BrowserHost.normalizeUserAgent userAgent

    let private arguments (fields: (string * JsonNode) list) =
        let value = JsonObject()
        for key, node in fields do value[key] <- node
        value

    let private invokeCommand (engine: Runtime.Engine) operation fields =
        let response = engine.Dispatch(operation, arguments fields)
        if not (response["ok"].GetValue<bool>()) then
            let code = (response["error"]["code"]).GetValue<string>()
            // Do not include request data, cookies or provider details in startup diagnostics.
            invalidOp $"Campfire application initialization failed: {operation}/{code}."
        response

    let private define engine (source: string) =
        invokeCommand engine "define"
            [ "frontend", JsonValue.Create("flow") :> JsonNode
              "syntaxVersion", JsonValue.Create(2) :> JsonNode
              "source", JsonValue.Create(source) :> JsonNode ] |> ignore

    let private commit engine name library =
        invokeCommand engine "commit"
            [ "word", JsonValue.Create(name: string) :> JsonNode
              "library", JsonValue.Create(library: bool) :> JsonNode ] |> ignore

    /// Installs fixed checked source only into a new empty dictionary. A loaded
    /// dictionary is used as-is; missing entrypoints fail instead of reseeding.
    /// Protocol commands below are startup-only constants, never request-facing.
    let create (projectDirectory: string) (sourceDirectory: string) (environment: HostOperationEnvironment) =
        let read name = File.ReadAllText(Path.Combine(sourceDirectory, name))
        let readMany names = names |> List.map read |> String.concat "\n\n"
        let current = Path.Combine(projectDirectory, ".agentlang", "store", "CURRENT")
        let store = Path.GetDirectoryName current
        let isNew = not (Directory.Exists store)
        if not isNew && not (File.Exists current) then
            invalidOp "Campfire dictionary is partial; refusing to reset persisted state."
        if isNew then
            if Directory.Exists projectDirectory && not (Directory.EnumerateFileSystemEntries(projectDirectory) |> Seq.isEmpty) then
                invalidOp "Campfire initialization requires an empty project directory."
            Directory.CreateDirectory projectDirectory |> ignore
            let bootstrap = Runtime.Engine(projectDirectory, capabilities, "2026-01-01T00:00:00Z", fileSystemMode = FileSystemMode.Virtual, testCapabilities = capabilities)
            define bootstrap (readMany
                [ "campfire-types.flow"; "session-policy.flow"; "authentication-types.flow"
                  "browser-types.flow"; "request-policy.flow"; "http-types.flow" ])
            commit bootstrap "utc-instant.valid?" true
            commit bootstrap "UtcInstant" false
            commit bootstrap "campfire.sessions.refresh-due?" true
            commit bootstrap "campfire.request.origin-valid?" true
            commit bootstrap "campfire.request.fetch-site-valid?" true
            commit bootstrap "campfire.request.forgery-check-passed?" true
            invokeCommand bootstrap "commit" [] |> ignore
        let engine = Runtime.Engine(projectDirectory, capabilities, "2026-01-01T00:00:00Z",
                                    fileSystemMode = FileSystemMode.Virtual, testCapabilities = capabilities,
                                    hostOperations = environment)
        if isNew then
            define engine (readMany
                [ "message-policy.flow"; "room-message.flow"; "room-message-tests.flow"
                  "authentication.flow"; "authentication-tests.flow"
                  "sign-in.flow"; "sign-in-tests.flow"
                  "sign-out.flow"; "sign-out-tests.flow"
                  "browser-policy.flow"; "browser-tests.flow"
                  "views.flow"; "layout.flow"; "views-tests.flow"
                  "http-login.flow"; "http.flow"; "http-login-tests.flow"; "http-tests.flow"; "http-logout-tests.flow"
                  "http-room-read-tests.flow" ])
            for name in
                [ "user.can-administer?"; "message.can-administer?"
                  "campfire.rooms.list-for-user"; "campfire.messages.page-for-member"
                  "campfire.messages.create-for-member"; "campfire.authentication.restore-session"
                  "campfire.authentication.sign-in"; "campfire.authentication.sign-out"
                  "campfire.browser.supported?" ] do
                commit engine name true
            invokeCommand engine "commit" [] |> ignore
        let entry = engine.BindFlowEntryPoint("campfire.http.dispatch", [ TNamed "CampfireHttpRequest" ], [ TNamed "CampfireHttpResponse" ])
        let invoke value =
            match entry.Invoke [ value ] with
            | Ok result ->
                match result.Values, result.Console with
                | [ response ], [] -> Ok response
                | _ -> Error "HTTP_ENTRYPOINT_CONTRACT_MISMATCH"
            | Error _ -> Error "HTTP_DISPATCH_FAILED"
        { Engine = engine; Invoke = invoke }
