namespace AgentLang.Campfire.Http

open System
open System.Collections.Generic
open AgentLang

/// A bounded in-process attempt store. One production provider owns one store;
/// tests construct fresh providers rather than sharing production counters.
module LoginHost =
    [<Literal>]
    let MaximumKeys = 4096

    [<Literal>]
    let WindowMicroseconds = 180000000L

    let operationId = HostOperationId "campfire.sessions.login-attempt-count"

    let manifest =
        Map.ofList
            [ operationId,
              { Id = operationId
                WordName = "campfire.sessions.login-attempt-count"
                Inputs = [ TNamed "RemoteIp"; TNamed "UtcInstant" ]
                Outputs = [ TResult(TInt, TNamed "DbError") ]
                Effects = Set.singleton "db.write"
                Documentation = "Increments the fixed-window in-memory login-attempt store. The first hit starts a three-minute window; counts saturate at eleven. At most 4096 live IP keys are retained; new keys at capacity are rejected with count eleven. State resets on provider/process restart." } ]

    let createProvider () : HostOperationProvider =
        let counters = Dictionary<string, int64 * int64>(StringComparer.Ordinal)
        let gate = obj()
        let result value = Ok [ ResultValue(TInt, TNamed "DbError", Ok(IntValue value)) ]
        let invoke operation values =
            if operation <> operationId then Error HostOperationFailure.MissingHandler
            else
                match values with
                | [ NamedValue("RemoteIp", StringValue ip); NamedValue("UtcInstant", IntValue now) ] when not (isNull ip) ->
                    lock gate (fun () ->
                        let expired =
                            counters
                            |> Seq.choose (fun item -> let _, expiry = item.Value in if expiry <= now then Some item.Key else None)
                            |> Seq.toArray
                        for key in expired do counters.Remove key |> ignore
                        match counters.TryGetValue ip with
                        | true, (count, expiry) ->
                            let next = min 11L (count + 1L)
                            counters[ip] <- next, expiry
                            result next
                        | false, _ when counters.Count >= MaximumKeys -> result 11L
                        | false, _ when now > Int64.MaxValue - WindowMicroseconds ->
                            Ok [ ResultValue(TInt, TNamed "DbError", Error(EnumValue("DbError", "invalid-input", None))) ]
                        | false, _ ->
                            counters.Add(ip, (1L, now + WindowMicroseconds))
                            result 1L)
                | _ -> Error HostOperationFailure.ProviderContractMismatch
        { OperationIds = Set.singleton operationId
          Invoke = invoke
          Dispose = fun () -> lock gate (fun () -> counters.Clear()) }
