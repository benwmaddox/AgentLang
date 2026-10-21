namespace AgentLang.FlowHttp

open System
open System.IO
open System.Text
open System.Text.Json.Nodes
open System.Text.RegularExpressions
open AgentLang
open AgentLang.Llvm

/// A Flow HTTP app loaded into an engine: the stdlib (src/stdlib/http.flow,
/// db.flow) plus the app's sources, committed, with the db host operations
/// bound to one SQLite connection (the interpreter's "worker").
type FlowApp =
    { Engine: Runtime.Engine
      App: string
      ContextType: string
      Provider: HostOperationProvider
      /// The interpreter "worker": its store and response stream.
      Worker: Store.WorkerState
      /// False when the app was defined but not committed: its attached tests have not run.
      Verified: bool }

    interface IDisposable with
        member this.Dispose() = this.Provider.Dispose()

module FlowApp =
    let capabilities = Set.ofList [ "db.read"; "db.write"; "cache.read"; "cache.write"; "clock.read"; "random.read"; "store.read"; "store.write"; "response.write"; "fs.read" ]
    /// Defined first, on a bootstrap engine: types the host manifest names, and words that call no host operation.
    let stdlibFiles = [ "http.flow"; "db.flow"; "cache.flow"; "rich-html.flow" ]
    /// Defined with the app, once the host operations are bound (their words call them).
    let hostStdlibFiles = [ "store.flow"; "worker-cache.flow" ]

    /// src/stdlib, found next to the executable (copied there) or in the repo.
    let stdlibDirectory () =
        let candidates =
            [ Path.Combine(AppContext.BaseDirectory, "stdlib")
              Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, "..", "stdlib")) ]
        candidates |> List.tryFind (fun dir -> File.Exists(Path.Combine(dir, "http.flow")))
        |> Option.defaultWith (fun () -> invalidOp "Flow stdlib directory (src/stdlib) not found.")

    let private dispatch (engine: Runtime.Engine) operation (fields: (string * JsonNode) list) =
        let args = JsonObject()
        for key, value in fields do args[key] <- value
        let response = engine.Dispatch(operation, args)
        if not (response["ok"].GetValue<bool>()) then
            failwith $"Flow {operation} failed: {response.ToJsonString()}"
        response

    let private defineFiles engine (files: (string * string) list) =
        // Each file is parsed on its own (the per-source limits), as one project.
        let rows = JsonArray()
        for file, source in files do
            let row = JsonObject()
            row["file"] <- JsonValue.Create(file)
            row["source"] <- JsonValue.Create(source)
            rows.Add(row)
        dispatch engine "define" [ "frontend", JsonValue.Create("flow") :> JsonNode; "syntaxVersion", JsonValue.Create(2) :> JsonNode; "sources", rows :> JsonNode ]

    let private define engine (source: string) =
        dispatch engine "define"
            [ "frontend", JsonValue.Create("flow") :> JsonNode
              "syntaxVersion", JsonValue.Create(2) :> JsonNode
              "source", JsonValue.Create(source) :> JsonNode ] |> ignore

    /// Comment-stripped source text, for source-level checks.
    let private code (source: string) =
        source.Split('\n') |> Array.map (fun line -> let index = line.IndexOf("//") in if index >= 0 then line.Substring(0, index) else line) |> String.concat "\n"

    /// The worker context type: the return type of <app>.worker-init.
    let contextTypeOf (app: string) (sources: string list) =
        let pattern = $@"(?m)^fn {Regex.Escape app}\.worker-init\(\s*[\w-]+\s*:\s*List<HttpHeader>\s*\)\s*->\s*([A-Z]\w*)"
        sources
        |> List.tryPick (fun source -> let m = Regex.Match(code source, pattern) in if m.Success then Some m.Groups[1].Value else None)
        |> Option.defaultWith (fun () -> failwith $"No `fn {app}.worker-init(config: List<HttpHeader>) -> <Context>` found.")

    /// Defines and commits the stdlib and app sources. `databasePath` backs
    /// the production db provider; attached tests get a fresh in-memory one.
    /// Phase timings on stderr when AGENTLANG_BUILD_TIMING is set (build-time profiling).
    let timed (label: string) (work: unit -> 'T) : 'T =
        if isNull (Environment.GetEnvironmentVariable "AGENTLANG_BUILD_TIMING") then work ()
        else
            let watch = Diagnostics.Stopwatch.StartNew()
            let result = work ()
            eprintfn "timing %s %.1fs" label watch.Elapsed.TotalSeconds
            result

    /// Loads an app. With `verify` false the app's words are defined (parsed, type-checked
    /// and IR-verified) but not committed, so their attached tests do not run: the fast,
    /// unverified build. Commit runs every attached test.
    let loadWith (verify: bool) (projectDirectory: string) (app: string) (sourcePaths: string list) (databasePath: string) (storeBytes: int) =
        let stdlib = stdlibDirectory ()
        let stdlibSources = stdlibFiles |> List.map (fun name -> File.ReadAllText(Path.Combine(stdlib, name)))
        let appSources = sourcePaths |> List.map File.ReadAllText
        for path, source in List.zip sourcePaths appSources do
            match Db.nonLiteralSqlCalls (code source) with
            | [] -> ()
            | calls -> failwith $"""{Path.GetFileName path}: db.query/db.execute need a string-literal SQL argument ({String.Join("; ", calls)})."""
        let contextType = contextTypeOf app appSources
        Directory.CreateDirectory projectDirectory |> ignore
        // Host operation types must exist before an engine binds the manifest.
        let bootstrap = Runtime.Engine(projectDirectory, capabilities, "2026-01-01T00:00:00Z", fileSystemMode = FileSystemMode.Virtual, testCapabilities = capabilities)
        timed "bootstrap-define" (fun () -> define bootstrap (String.concat "\n\n" stdlibSources))
        timed "bootstrap-commit" (fun () -> dispatch bootstrap "commit" [] |> ignore)
        // db: one connection (this interpreter "worker"); cache: the process-wide
        // store. Attached tests get an in-memory database and a fresh cache.
        // store: this worker's region (storeBytes, 0 = no store); tests get a fresh one each.
        let worker = Store.WorkerState(storeBytes, Store.streamCapacity)
        let provider = Cache.combine [ Db.createProvider databasePath; Cache.createProvider (Some Cache.processStore); Store.createProvider worker ]
        let merge (maps: Map<HostOperationId, HostOperationDefinition> list) = maps |> List.reduce (fun acc next -> Map.fold (fun a k v -> Map.add k v a) acc next)
        let environment =
            { Manifest = merge [ Db.manifest; Cache.manifest; Store.manifest ]
              ProductionProvider = provider
              CreateTestProvider =
                fun () ->
                    Ok(Cache.combine
                        [ Db.createProvider ":memory:"
                          Cache.createProvider (Some(Collections.Concurrent.ConcurrentDictionary<string, string>(StringComparer.Ordinal)))
                          Store.createProvider (Store.WorkerState(Store.testCapacity, Store.streamCapacity)) ]) }
        let engine =
            Runtime.Engine(projectDirectory, capabilities, "2026-01-01T00:00:00Z",
                           fileSystemMode = FileSystemMode.Virtual, testCapabilities = capabilities, hostOperations = environment)
        let hostStdlibSources = hostStdlibFiles |> List.map (fun name -> File.ReadAllText(Path.Combine(stdlib, name)))
        let named = (List.zip (hostStdlibFiles |> List.map (fun name -> "stdlib/" + name)) hostStdlibSources) @ (List.zip (sourcePaths |> List.map Path.GetFileName) appSources)
        timed "define" (fun () -> defineFiles engine named |> ignore)
        if verify then timed "commit" (fun () -> dispatch engine "commit" [] |> ignore)
        { Engine = engine; App = app; ContextType = contextType; Provider = provider; Worker = worker; Verified = verify }

    let loadWithStore projectDirectory app sourcePaths databasePath storeBytes = loadWith true projectDirectory app sourcePaths databasePath storeBytes

    /// load with no worker store (store.capacity is 0).
    let load projectDirectory app sourcePaths databasePath = loadWithStore projectDirectory app sourcePaths databasePath 0

    let workerInitSignature (app: FlowApp) = [ TList(TNamed "HttpHeader") ], [ TNamed app.ContextType ]
    let handleSignature (app: FlowApp) = [ TNamed "HttpRequest"; TNamed app.ContextType ], [ TNamed "HttpResponse" ]

    let private invoke (app: FlowApp) word (inputs, outputs) (values: Value list) =
        match app.Engine.BindFlowEntryPoint(word, inputs, outputs).Invoke values with
        | Ok result -> List.exactlyOne result.Values
        | Error diagnostic -> failwith $"{word}: {diagnostic.Code} {diagnostic.Message}"

    let header (name: string) (value: string) = RecordValue("HttpHeader", Map.ofList [ "name", StringValue name; "value", StringValue value ])

    let workerInit (app: FlowApp) (config: (string * string) list) =
        invoke app $"{app.App}.worker-init" (workerInitSignature app) [ ListValue(TNamed "HttpHeader", config |> List.map (fun (n, v) -> header n v)) ]

    let request (method: string) (target: string) (headers: (string * string) list) (body: byte[]) (remoteIp: string) (nowMicroseconds: int64) =
        RecordValue(
            "HttpRequest",
            Map.ofList
                [ "method", StringValue method
                  "target", StringValue target
                  "headers", ListValue(TNamed "HttpHeader", headers |> List.map (fun (n, v) -> header (n.ToLowerInvariant()) v))
                  "body", BytesValue body
                  "remote-ip", StringValue remoteIp
                  "now-microseconds", IntValue nowMicroseconds ])

    /// One request, as the native host runs it: the response stream starts
    /// empty, and a `streamed` response's body is the stream followed by `body`.
    let handle (app: FlowApp) (request: Value) (context: Value) =
        app.Worker.ResetStream()
        match invoke app $"{app.App}.handle" (handleSignature app) [ request; context ] with
        | RecordValue("HttpResponse", fields) when fields.TryFind "streamed" = Some(BoolValue true) ->
            let tail = match fields.TryFind "body" with Some(StringValue text) -> text | _ -> ""
            let streamed = Encoding.UTF8.GetString(app.Worker.Stream.ToArray())
            RecordValue("HttpResponse", fields.Add("body", StringValue(streamed + tail)))
        | response -> response

    /// Native host manifest for the db and cache operations (no app operations).
    let hostManifest (app: FlowApp) : OwningAppHostManifest =
        { ProfileId = "flow-http-v1"
          EntryWord = $"{app.App}.handle"
          EntryInputTypes = [ "HttpRequest"; app.ContextType ]
          EntryOutputTypes = [ "HttpResponse" ]
          AdapterSymbol = "al_host_flow_http_v1"
          HeaderPath = ""
          AdapterFileName = "flow_http_db.c"
          OutputLimitBytes = 16777216u
          BoundarySchemaNames = [ "HttpHeader"; "HttpRequest"; "HttpResponse"; "DbValue"; "DbRow"; "SqlError"; "DbValueKind"; app.ContextType ]
          Operations =
            Db.nativeOperations @ Cache.nativeOperations @ Store.nativeOperations
            |> List.map (fun (tag, name, inputs, outputs, effects) ->
                { Name = name; Tag = tag; InputTypes = inputs; OutputTypes = outputs; Effects = effects }) }

    let private exportEntryPoint (app: FlowApp) word inputs outputs =
        if app.Verified then app.Engine.ExportVerifiedFlowEntryPoint(word, inputs, outputs)
        else app.Engine.ExportUnverifiedFlowEntryPoint(word, inputs, outputs)

    /// Emits the two-entry native module (flow_http_worker_init, flow_http_handle)
    /// for a target. Returns the LLVM IR and the diagnostic table.
    let emitNative (app: FlowApp) (runtimeProfile: OwningRuntimeProfile) (target: OwningTarget) =
        let initInputs, initOutputs = workerInitSignature app
        let handleInputs, handleOutputs = handleSignature app
        let initBody = exportEntryPoint app $"{app.App}.worker-init" initInputs initOutputs
        let handleBody = exportEntryPoint app $"{app.App}.handle" handleInputs handleOutputs
        // The handle shim leads: the host profile checks the head entry
        // against the manifest's entry word and boundary types.
        OwningStackAot.emitHostModule runtimeProfile target (OwningNativeHostProfile.AppHost(hostManifest app))
            [ { ExportName = "flow_http_handle"; EntryWord = $"{app.App}.handle"; Body = handleBody; BorrowsLastInput = true }
              { ExportName = "flow_http_worker_init"; EntryWord = $"{app.App}.worker-init"; Body = initBody; BorrowsLastInput = false } ]

    /// Word symbol map of the module emitNative emits (agentlang_word_NNNN -> word).
    let nativeSymbolsText (app: FlowApp) =
        let initInputs, initOutputs = workerInitSignature app
        let handleInputs, handleOutputs = handleSignature app
        let initBody = exportEntryPoint app $"{app.App}.worker-init" initInputs initOutputs
        let handleBody = exportEntryPoint app $"{app.App}.handle" handleInputs handleOutputs
        let builder = StringBuilder()
        OwningStackAot.hostModuleSymbols (OwningNativeHostProfile.AppHost(hostManifest app))
            [ { ExportName = "flow_http_handle"; EntryWord = $"{app.App}.handle"; Body = handleBody; BorrowsLastInput = true }
              { ExportName = "flow_http_worker_init"; EntryWord = $"{app.App}.worker-init"; Body = initBody; BorrowsLastInput = false } ]
        |> List.iter (fun (symbol, name, revision) -> builder.Append($"{symbol}\t{name}\t{revision}\n") |> ignore)
        builder.ToString()

    /// Loads an app for a verified native build (report 303): defines it, checks that
    /// everything reachable from the native entries has a native lowering (the owning
    /// backend's validation, without emitting), and only then commits, which runs
    /// every attached test. A primitive without a native lowering fails in seconds,
    /// not after the tests.
    let loadNativeVerified projectDirectory app sourcePaths databasePath storeBytes =
        let defined = loadWith false projectDirectory app sourcePaths databasePath storeBytes
        try timed "native-check" (fun () -> nativeSymbolsText defined |> ignore)
        with LanguageException diagnostic ->
            let word = defaultArg diagnostic.Word "?"
            failwith $"Native check, before the attached tests: {diagnostic.Code} in {word}: {diagnostic.Message} {String.Join(' ', diagnostic.Actual)}"
        timed "commit" (fun () -> dispatch defined.Engine "commit" [] |> ignore)
        { defined with Verified = true }

    let diagnosticsText (diagnostics: Diagnostic array) =
        let builder = StringBuilder()
        diagnostics |> Array.iteri (fun index diagnostic ->
            builder.Append(index + 1).Append('\t').Append(diagnostic.Code).Append('\t')
                .Append(defaultArg diagnostic.Word "").Append('\t').Append(diagnostic.Message.Replace('\t', ' ').Replace('\n', ' ')).Append('\n') |> ignore)
        builder.ToString()
