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

    let private testMissingReminder () =
        let path = Invoice.reminderPath (InvoiceId "INV-MISSING")
        let files = VirtualFiles Map.empty
        let invoice =
            { Id = InvoiceId "INV-MISSING"
              Status = InvoiceStatus "open" }

        let result = ReminderOperations.queueOnce (files :> IReminderFiles) invoice
        assertEqual "queued" result
        assertEqual (Map.ofList [ path, "queued" ]) files.State
        assertEqual 1 files.ReadCount
        assertEqual 1 files.WriteCount

    let private testExistingQueuedReminder () =
        let path = Invoice.reminderPath (InvoiceId "INV-QUEUED")
        let initial = Map.ofList [ path, "queued" ]
        let files = VirtualFiles initial
        let invoice =
            { Id = InvoiceId "INV-QUEUED"
              Status = InvoiceStatus "open" }

        let result = ReminderOperations.queueOnce (files :> IReminderFiles) invoice
        assertEqual "queued" result
        assertEqual initial files.State
        assertEqual 2 files.ReadCount
        assertEqual 0 files.WriteCount

    let private testExistingDifferentMarker () =
        let path = Invoice.reminderPath (InvoiceId "INV-MARKER")
        let initial = Map.ofList [ path, "sent-by-operator" ]
        let files = VirtualFiles initial
        let invoice =
            { Id = InvoiceId "INV-MARKER"
              Status = InvoiceStatus "open" }

        let result = ReminderOperations.queueOnce (files :> IReminderFiles) invoice
        assertEqual "sent-by-operator" result
        assertEqual initial files.State
        assertEqual 2 files.ReadCount
        assertEqual 0 files.WriteCount

    let private testNonOpenInvoice () =
        let path = Invoice.reminderPath (InvoiceId "INV-CLOSED")
        let initial = Map.ofList [ path, "existing-marker" ]
        let files = VirtualFiles initial
        let invoice =
            { Id = InvoiceId "INV-CLOSED"
              Status = InvoiceStatus "Open" }

        let result = ReminderOperations.queueOnce (files :> IReminderFiles) invoice
        assertEqual "not-open" result
        assertEqual initial files.State
        assertEqual 0 files.ReadCount
        assertEqual 0 files.WriteCount

    /// Runs an open invoice example through the seeded execution wrapper.
    let queueReminderExample () =
        let path = Invoice.reminderPath (InvoiceId "INV-EXAMPLE")
        let files = VirtualFiles Map.empty
        let invoice =
            { Id = InvoiceId "INV-EXAMPLE"
              Status = InvoiceStatus "open" }

        let result =
            Execution.run true true (files :> IReminderFiles) invoice ReminderOperations.queueOnce
        assertEqual "queued" result
        assertEqual (Map.ofList [ path, "queued" ]) files.State
        printfn "EXAMPLE_QUEUE_REMINDER=%s" result

    let runOwnTests () =
        testMissingReminder ()
        testExistingQueuedReminder ()
        testExistingDifferentMarker ()
        testNonOpenInvoice ()
        queueReminderExample ()
        printfn "OWN_TESTS_PASSED=4"

    [<EntryPoint>]
    let main _ =
        runSeedTests ()
        runOwnTests ()
        0