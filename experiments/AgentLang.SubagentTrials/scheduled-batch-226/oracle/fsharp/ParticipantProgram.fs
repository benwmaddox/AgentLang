namespace AgentLang.SubscriptionHandoff.Control

module ParticipantProgram =
    [<EntryPoint>]
    let main args =
        try
            let arguments = Array.toList args
            let rec find option = function
                | [] -> failwithf "Missing required argument %s" option
                | key :: value :: _ when key = option -> value
                | _ :: rest -> find option rest
            Scorer.runBatchFileWith AgentLang.SubscriptionHandoff.Subscription.renewScheduledBatch (find "--cases" arguments)
            0
        with error ->
            System.Console.Error.WriteLine(error.ToString())
            2
