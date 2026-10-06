namespace AgentLang.StatefulPilot

module SelfTests =
    let private assertEqual expected actual =
        if actual <> expected then
            failwithf "Expected %A but received %A" expected actual

    // <immutable-seed-tests>
    let runSeedTests () =
        assertEqual
            "outbox/invoice-reminders/INV-001"
            (Invoice.reminderPath (InvoiceId "INV-001"))
        assertEqual
            "outbox/invoice-reminders/inv.alpha_2"
            (Invoice.reminderPath (InvoiceId "inv.alpha_2"))
        printfn "Seed helper tests passed: 2"
    // </immutable-seed-tests>

    // Add operation tests and the runnable example here, then invoke them below.
    let runOwnTests () =
        ()

    [<EntryPoint>]
    let main _ =
        runSeedTests ()
        runOwnTests ()
        0