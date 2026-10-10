namespace AgentLang.SubscriptionHandoff.Control

open System

module Program =
    [<EntryPoint>]
    let main args =
        try
            let arguments = Array.toList args
            let rec find option = function
                | [] -> failwithf "Missing required argument %s" option
                | key :: value :: _ when key = option -> value
                | _ :: rest -> find option rest
            let control = find "--control" arguments
            let dryRun =
                match Boolean.TryParse(find "--dry-run" arguments) with
                | true, value -> value
                | false, _ -> failwith "--dry-run must be true or false"
            let cases = find "--cases" arguments
            Scorer.runFile control dryRun cases
            0
        with error ->
            Console.Error.WriteLine(error.Message)
            2
