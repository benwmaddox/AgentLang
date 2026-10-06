namespace AgentLang.EarlyPilot

module SelfTests =
    // Each task's agent supplies relevant self-tests. Hidden acceptance runs
    // outside the editable fixture; a successful empty runner is not acceptance.
    [<EntryPoint>]
    let main _ =
        printfn "No agent self-tests have been added."
        0
