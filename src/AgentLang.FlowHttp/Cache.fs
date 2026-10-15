namespace AgentLang.FlowHttp

open System.Collections.Concurrent
open AgentLang

/// The process-wide fragment cache as host operations (src/stdlib/cache.flow):
///   cache.get(key: String) -> Option<String>   effects cache.read
///   cache.put(key: String, value: String) -> Bool   effects cache.write
/// put is first-writer-wins: a put of a key already present (including a
/// racing put) is dropped and returns false. The native host backs this with
/// a lock-free table shared by every worker (native/http_host/
/// flow_http_cache.c, size-bounded by --cache-mb, 0 disables it); the
/// interpreter backs it with a ConcurrentDictionary of the same semantics,
/// unbounded.
module Cache =
    let getId = HostOperationId "cache.get"
    let putId = HostOperationId "cache.put"
    let operationIds = Set.ofList [ getId; putId ]

    let manifest : Map<HostOperationId, HostOperationDefinition> =
        Map.ofList
            [ getId,
              { Id = getId
                WordName = "cache.get"
                Inputs = [ TString ]
                Outputs = [ TOption TString ]
                Effects = Set.singleton "cache.read"
                Documentation = "The value cached under key in the process-wide cache, if any (a hit is copied into the request)." }
              putId,
              { Id = putId
                WordName = "cache.put"
                Inputs = [ TString; TString ]
                Outputs = [ TBool ]
                Effects = Set.singleton "cache.write"
                Documentation = "Caches value under key unless the key is present (first writer wins); true when stored." } ]

    /// Native host manifest operations: tag, name, boundary type names, effects.
    let nativeOperations =
        [ 3u, "cache.get", [ "String" ], [ "Option<String>" ], [ "cache.read" ]
          4u, "cache.put", [ "String"; "String" ], [ "Bool" ], [ "cache.write" ] ]

    /// The interpreter's process-wide store (all interpreter "workers").
    let processStore = ConcurrentDictionary<string, string>(System.StringComparer.Ordinal)

    /// A provider over `store`; None is a disabled cache (every get misses,
    /// every put is dropped), like --cache-mb 0.
    let createProvider (store: ConcurrentDictionary<string, string> option) : HostOperationProvider =
        let invoke id (arguments: Value list) =
            match id, arguments with
            | _, [ StringValue key ] when id = getId ->
                let found =
                    match store with
                    | Some store ->
                        match store.TryGetValue key with
                        | true, value -> Some(StringValue value)
                        | _ -> None
                    | None -> None
                Ok [ OptionValue(TString, found) ]
            | _, [ StringValue key; StringValue value ] when id = putId ->
                let stored = match store with Some store -> store.TryAdd(key, value) | None -> false
                Ok [ BoolValue stored ]
            | _ when operationIds.Contains id -> Error HostOperationFailure.ProviderContractMismatch
            | _ -> Error HostOperationFailure.MissingHandler
        { OperationIds = operationIds; Invoke = invoke; Dispose = ignore }

    /// One provider answering every operation of the given providers.
    let combine (providers: HostOperationProvider list) : HostOperationProvider =
        { OperationIds = providers |> List.map (fun p -> p.OperationIds) |> Set.unionMany
          Invoke =
            fun id arguments ->
                match providers |> List.tryFind (fun p -> p.OperationIds.Contains id) with
                | Some provider -> provider.Invoke id arguments
                | None -> Error HostOperationFailure.MissingHandler
          Dispose = fun () -> for provider in providers do provider.Dispose() }
