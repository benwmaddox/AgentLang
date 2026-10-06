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
        let invoice status = { Id = InvoiceId "control-1"; Status = InvoiceStatus status }
        let path = Invoice.reminderPath (InvoiceId "control-1")
        let invoke (files: VirtualFiles) status =
            Execution.run true true (files :> IReminderFiles) (invoice status) ReminderOperations.queueOnce
        let missing = VirtualFiles(Map.empty)
        assertEqual "queued" (invoke missing "open")
        assertEqual (Some "queued") (Map.tryFind path missing.State)
        assertEqual (1,1) (missing.ReadCount, missing.WriteCount)
        let queued = VirtualFiles(Map.ofList [path,"queued"])
        assertEqual "queued" (invoke queued "open")
        assertEqual (Map.ofList [path,"queued"]) queued.State
        assertEqual (2,0) (queued.ReadCount,queued.WriteCount)
        let other = VirtualFiles(Map.ofList [path,"sent-earlier"])
        assertEqual "sent-earlier" (invoke other "open")
        assertEqual (Map.ofList [path,"sent-earlier"]) other.State
        assertEqual (2,0) (other.ReadCount,other.WriteCount)
        let closed = VirtualFiles(Map.empty)
        assertEqual "not-open" (invoke closed "Open")
        assertEqual Map.empty closed.State
        assertEqual (0,0) (closed.ReadCount,closed.WriteCount)
        printfn "OWN_TESTS_PASSED=4"
        let example = VirtualFiles(Map.empty)
        assertEqual "queued" (invoke example "open")
        printfn "EXAMPLE_QUEUE_REMINDER=queued"

    [<EntryPoint>]
    let main _ =
        runSeedTests ()
        runOwnTests ()
        0