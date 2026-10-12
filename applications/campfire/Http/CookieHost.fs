namespace AgentLang.Campfire.Http

open System
open AgentLang
open AgentLang.Campfire.Cookies

/// Deterministic verification only. Signing and encrypted cookie mutation belong
/// to the transport finalizer; no secret material enters the language dictionary.
module CookieHost =
    let private verifyId = HostOperationId "campfire.cookies.verify-session-token"

    let manifest =
        Map.ofList
            [ verifyId,
              { Id = verifyId
                WordName = "campfire.cookies.verify-session-token"
                Inputs = [ TString; TNamed "UtcInstant" ]
                Outputs = [ TOption(TNamed "SessionToken") ]
                Effects = Set.empty
                Documentation = "Verifies the exact Rails signed session cookie at a caller-supplied validated UTC instant. Invalid cookies return none." } ]

    let createProvider (cookies: CookieService) : HostOperationProvider =
        let invoke operation values =
            match operation, values with
            | id, [ StringValue signed; NamedValue("UtcInstant", IntValue microseconds) ] when id = verifyId ->
                try
                    let now = DateTimeOffset.UnixEpoch.AddTicks(Checked.(*) microseconds 10L)
                    let token = cookies.VerifySessionToken(signed, now)
                    Ok [ OptionValue(TNamed "SessionToken", token |> Option.map (fun value -> NamedValue("SessionToken", StringValue value))) ]
                with _ -> Error HostOperationFailure.InvocationFailed
            | id, _ when id = verifyId -> Error HostOperationFailure.ProviderContractMismatch
            | _ -> Error HostOperationFailure.MissingHandler
        { OperationIds = Set.singleton verifyId
          Invoke = invoke
          Dispose = ignore }

/// Each operation has exactly one registered owner. Composition fails rather
/// than allowing a later provider to replace a production operation implicitly.
module HostComposition =
    let manifest (parts: Map<HostOperationId, HostOperationDefinition> list) =
        (Map.empty, parts)
        ||> List.fold (fun combined part ->
            (combined, part |> Map.toList)
            ||> List.fold (fun result (id, definition) ->
                if Map.containsKey id result then invalidArg "parts" "Duplicate host operation identity."
                Map.add id definition result))

    let provider (parts: HostOperationProvider list) : HostOperationProvider =
        let owners =
            (Map.empty, parts)
            ||> List.fold (fun combined part ->
                (combined, part.OperationIds)
                ||> Set.fold (fun result id ->
                    if Map.containsKey id result then invalidArg "parts" "Duplicate host provider identity."
                    Map.add id part result))
        { OperationIds = owners |> Map.keys |> Set.ofSeq
          Invoke = fun id values ->
              match Map.tryFind id owners with
              | Some owner -> owner.Invoke id values
              | None -> Error HostOperationFailure.MissingHandler
          Dispose = fun () ->
              for part in parts do part.Dispose() }
