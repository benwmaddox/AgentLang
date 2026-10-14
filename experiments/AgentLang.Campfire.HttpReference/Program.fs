module CampfireNativeHttp253.ReferenceHost

open System
open System.IO
open System.Text.Json
open System.Threading
open AgentLang
open AgentLang.Campfire.Authentication.Host
open AgentLang.Campfire.Browser.Host
open AgentLang.Campfire.Cookies
open AgentLang.Campfire.Host
open AgentLang.Campfire.Http
open AgentLang.Campfire.Persistence
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Hosting
open Microsoft.Extensions.Logging

type private Options =
    { DatabasePath: string
      SecretPath: string
      ProjectPath: string
      SourcePath: string
      Port: int
      StopEventName: string }

[<Sealed>]
type private SystemClock() =
    interface IClock with
        member _.UtcNow() = DateTimeOffset.UtcNow

module private Host =
    let private usage =
        "Usage: ReferenceHost.dll --db <copied-fixture.sqlite> --secret-file <private-secret> --project <private-dictionary-dir> --source <applications/campfire> [--port 13774] [--stop-event Local\\CampfireNativeHttp253ReferenceStop]"

    let private parseArguments (arguments: string array) =
        let values = System.Collections.Generic.Dictionary<string, string>(StringComparer.Ordinal)
        let mutable index = 0
        let mutable invalid = false
        while index < arguments.Length do
            let key = arguments[index]
            if not (key.StartsWith("--", StringComparison.Ordinal)) || index + 1 >= arguments.Length || values.ContainsKey key then
                invalid <- true
                index <- arguments.Length
            else
                values.Add(key, arguments[index + 1])
                index <- index + 2
        let value key = match values.TryGetValue key with true, found -> Some found | _ -> None
        let parsePort (raw: string) =
            match Int32.TryParse raw with
            | true, port when port >= 1 && port <= 65535 -> Some port
            | _ -> None
        let allowed = set [ "--db"; "--secret-file"; "--project"; "--source"; "--port"; "--stop-event" ]
        if invalid || (values.Keys |> Seq.exists (allowed.Contains >> not)) then Error usage
        else
            match value "--db", value "--secret-file", value "--project", value "--source" with
            | Some database, Some secret, Some project, Some source ->
                let port = value "--port" |> Option.defaultValue "13774" |> parsePort
                let stopName = value "--stop-event" |> Option.defaultValue "Local\\CampfireNativeHttp253ReferenceStop"
                match port with
                | Some parsedPort when not (String.IsNullOrWhiteSpace stopName) ->
                    Ok
                        { DatabasePath = Path.GetFullPath database
                          SecretPath = Path.GetFullPath secret
                          ProjectPath = Path.GetFullPath project
                          SourcePath = Path.GetFullPath source
                          Port = parsedPort
                          StopEventName = stopName }
                | _ -> Error usage
            | _ -> Error usage

    let private verifyInputs options =
        if not (File.Exists options.DatabasePath) then invalidArg "--db" "The copied fixture database must already exist."
        if not (File.Exists options.SecretPath) then invalidArg "--secret-file" "The private secret file must already exist."
        if not (File.Exists(Path.Combine(options.SourcePath, "http.flow"))) then invalidArg "--source" "The Campfire source directory is incomplete."
        if Directory.Exists options.ProjectPath && Directory.EnumerateFileSystemEntries(options.ProjectPath) |> Seq.isEmpty |> not then
            let store = Path.Combine(options.ProjectPath, ".agentlang", "store", "CURRENT")
            if not (File.Exists store) then invalidArg "--project" "Use a new empty project directory or a complete existing dictionary."

    let private createEnvironment databasePath clock cookies secretKeyBase =
        let expectProvider label = function
            | Ok provider -> provider
            | Error _ -> invalidOp ($"REFERENCE_PROVIDER_UNAVAILABLE:{label}")
        let campfire = CampfireHost.createProvider databasePath clock secretKeyBase |> expectProvider "campfire"
        let authentication = CampfireAuthenticationHost.createProvider databasePath |> expectProvider "authentication"
        let cookieProvider = CookieHost.createProvider cookies
        let browser = BrowserHost.createProvider()
        let login = LoginHost.createProvider()
        let parts = [ campfire; authentication; cookieProvider; browser; login ]
        let provider = HostComposition.provider parts
        let manifest =
            HostComposition.manifest
                [ CookieHost.manifest
                  CampfireHost.manifest
                  CampfireAuthenticationHost.manifest
                  BrowserHost.manifest
                  LoginHost.manifest ]
        let environment =
            { Manifest = manifest
              ProductionProvider = provider
              CreateTestProvider = fun () -> Error HostOperationFailure.TestProviderUnavailable }
        environment, provider

    let run arguments =
        match parseArguments arguments with
        | Error message ->
            Console.Error.WriteLine message
            2
        | Ok options ->
            try
                verifyInputs options
                let secret = File.ReadAllText options.SecretPath
                if String.IsNullOrEmpty secret then invalidOp "REFERENCE_SECRET_EMPTY"
                use cookies =
                    match CookieService.Create secret with
                    | Ok service -> service
                    | Error _ -> invalidOp "REFERENCE_COOKIE_SERVICE_INIT_FAILED"
                let clock = SystemClock()
                let environment, provider = createEnvironment options.DatabasePath (clock :> IClock) cookies secret
                try
                    let application = Application.create options.ProjectPath options.SourcePath environment
                    let authority = Uri($"http://127.0.0.1:{options.Port}/", UriKind.Absolute)
                    let transportOptions =
                        { PublicBaseUri = authority
                          ForceSsl = false
                          Cookies = cookies
                          Clock = fun () -> (clock :> IClock).UtcNow()
                          NormalizeBrowser = Application.normalizeBrowser
                          Invoke = application.Invoke }
                    let stopEvent = new EventWaitHandle(false, EventResetMode.ManualReset, options.StopEventName)
                    use stopEvent = stopEvent
                    stopEvent.Reset() |> ignore
                    let cancelHandler =
                        ConsoleCancelEventHandler(fun _ event ->
                            event.Cancel <- true
                            stopEvent.Set() |> ignore)
                    Console.CancelKeyPress.AddHandler cancelHandler
                    try
                        let builder = WebApplication.CreateBuilder([||])
                        builder.WebHost.UseUrls(authority.AbsoluteUri) |> ignore
                        builder.Logging.ClearProviders() |> ignore
                        use web = builder.Build()
                        Transport.mapRoutes web transportOptions
                        web.StartAsync().GetAwaiter().GetResult()
                        let ready =
                            {| status = "READY"
                               implementation = ".NET/Kestrel + interpreted language policy"
                               url = authority.AbsoluteUri
                               pid = Environment.ProcessId
                               stopEvent = options.StopEventName |}
                        Console.Out.WriteLine(JsonSerializer.Serialize ready)
                        Console.Out.Flush()
                        stopEvent.WaitOne() |> ignore
                        web.StopAsync().GetAwaiter().GetResult()
                        Console.Out.WriteLine("STOPPED")
                        Console.Out.Flush()
                        0
                    finally
                        Console.CancelKeyPress.RemoveHandler cancelHandler
                        stopEvent.Set() |> ignore
                finally
                    provider.Dispose()
            with
            | :? ArgumentException as error ->
                Console.Error.WriteLine($"STARTUP_FAILED:{error.ParamName}")
                2
            | _ ->
                Console.Error.WriteLine("STARTUP_FAILED:REFERENCE_HOST")
                1

[<EntryPoint>]
let main arguments = Host.run arguments
