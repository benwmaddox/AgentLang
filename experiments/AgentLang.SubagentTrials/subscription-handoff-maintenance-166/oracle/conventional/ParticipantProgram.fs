namespace AgentLang.SubscriptionHandoff.Control

open System

module ParticipantProgram =
    [<EntryPoint>]
    let main args =
        try
            let arguments = Array.toList args
            let rec find option = function
                | [] -> failwithf "Missing required argument %s" option
                | key :: value :: _ when key = option -> value
                | _ :: rest -> find option rest
            let cases = find "--cases" arguments
            match find "--operation" arguments with
            | "handoff" ->
                let dryRun =
                    match Boolean.TryParse(find "--dry-run" arguments) with
                    | true, value -> value
                    | false, _ -> failwith "--dry-run must be true or false"
                Scorer.runFileWith AgentLang.SubscriptionHandoff.Subscription.handoff dryRun cases
            | "renew-annual" ->
                Scorer.runRenewalFileWith AgentLang.SubscriptionHandoff.Subscription.renewAnnual cases
            | "renew-monthly" ->
                Scorer.runRenewalFileWith AgentLang.SubscriptionHandoff.Subscription.renewMonthly cases
            | name -> failwithf "Unknown operation: %s" name
            0
        with error ->
            Console.Error.WriteLine(error.Message)
            2
