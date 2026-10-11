namespace AgentLang.SubscriptionHandoff

open System

module Program =
    [<EntryPoint>]
    let main _ =
        try
            SelfTests.run ()
            0
        with error ->
            Console.Error.WriteLine(error.Message)
            1
