module AgentLang.Campfire.LoginRate.Tests

open System
open System.Threading.Tasks
open AgentLang
open AgentLang.Campfire.Http

let mutable private assertions = 0
let private check condition message =
    assertions <- assertions + 1
    if not condition then failwith message

let private count (provider: HostOperationProvider) ip now =
    match provider.Invoke LoginHost.operationId [ NamedValue("RemoteIp", StringValue ip); NamedValue("UtcInstant", IntValue now) ] with
    | Ok [ ResultValue(TInt, TNamed "DbError", Ok(IntValue number)) ] -> number
    | _ -> failwith "unexpected attempt-store result"

[<EntryPoint>]
let main _ =
    try
        let provider = LoginHost.createProvider ()
        check (provider.OperationIds = Set.singleton LoginHost.operationId) "only the fixed operation is registered"
        check (LoginHost.manifest[LoginHost.operationId].Effects = Set.singleton "db.write") "attempt-store mutation has an explicit effect"
        for expected in 1L..11L do
            check (count provider "192.0.2.1" (expected * 1000000L) = expected) "the first eleven attempts increment in one fixed window"
        check (count provider "192.0.2.1" 100000000L = 11L) "excess counts saturate without overflow"
        check (count provider "192.0.2.2" 100000000L = 1L) "IP keys are independent"
        check (count provider "192.0.2.1" 180999999L = 11L) "the first-hit window remains active just before expiry"
        check (count provider "192.0.2.1" 181000000L = 1L) "the window expires at exactly three minutes after its first hit"
        check (count provider "192.0.2.1" 181000001L = 2L) "later attempts did not slide the original expiry"
        check (count (LoginHost.createProvider ()) "192.0.2.1" 181000001L = 1L) "fresh providers have isolated attempt state"
        check (provider.Invoke (HostOperationId "campfire.arbitrary") [] = Error HostOperationFailure.MissingHandler) "unknown operations are rejected"
        check (provider.Invoke LoginHost.operationId [ StringValue "192.0.2.1"; IntValue 0L ] = Error HostOperationFailure.ProviderContractMismatch) "nominal metadata is required"
        let bounded = LoginHost.createProvider ()
        for index in 1..LoginHost.MaximumKeys do check (count bounded (string index) 0L = 1L) "live capacity admits each distinct key"
        check (count bounded "new" 1L = 11L) "capacity exhaustion rejects an untracked key without allocating it"
        check (count bounded "1" 1L = 2L) "existing keys remain counted at capacity"
        check (count bounded "new" LoginHost.WindowMicroseconds = 1L) "expiry reclaims capacity for a new key"
        let concurrent = LoginHost.createProvider ()
        let outcomes = Array.zeroCreate<int64> 32
        Parallel.For(0, outcomes.Length, fun index -> outcomes[index] <- count concurrent "shared" 0L) |> ignore
        check ((outcomes |> Array.filter (fun value -> value <= 10L)).Length = 10) "concurrent increments admit exactly the ten-attempt budget"
        check (count concurrent "shared" 1L = 11L) "concurrent updates retain the saturated count"
        printfn "Campfire login attempt store passed: %d assertions" assertions
        0
    with error ->
        eprintfn "Campfire login attempt store failed: %s" error.Message
        1
