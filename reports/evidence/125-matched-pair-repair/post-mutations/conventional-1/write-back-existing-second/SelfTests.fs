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

    let private makeInvoice id status =
        { Id = InvoiceId id
          Status = InvoiceStatus status }

    let private queue (files: VirtualFiles) (invoice: Invoice) =
        ReminderOperations.queueOnce (files :> IReminderFiles) invoice

    let private runMissingReminderTest () =
        let files = VirtualFiles(Map.empty)
        let invoice = makeInvoice "INV-MISSING" "open"
        let path = Invoice.reminderPath invoice.Id

        assertEqual "queued" (queue files invoice)
        assertEqual "queued" (Map.find path files.State)
        assertEqual 1 files.ReadCount
        assertEqual 1 files.WriteCount

        files.ResetCounts()
        assertEqual "queued" (queue files invoice)
        assertEqual 2 files.ReadCount
        assertEqual 0 files.WriteCount
        assertEqual "queued" (Map.find path files.State)

    let private runExistingQueuedMarkerTest () =
        let path = "outbox/invoice-reminders/INV-QUEUED"
        let files = VirtualFiles(Map.ofList [ path, "queued" ])
        let invoice = makeInvoice "INV-QUEUED" "open"

        assertEqual "queued" (queue files invoice)
        assertEqual 2 files.ReadCount
        assertEqual 0 files.WriteCount

    let private runExistingCustomMarkerTest () =
        let path = "outbox/invoice-reminders/INV-CUSTOM"
        let unrelatedPath = "notes/keep"
        let files =
            VirtualFiles(
                Map.ofList
                    [ path, "already sent"
                      unrelatedPath, "preserve" ]
            )
        let invoice = makeInvoice "INV-CUSTOM" "open"

        assertEqual "already sent" (queue files invoice)
        assertEqual 2 files.ReadCount
        assertEqual 0 files.WriteCount
        assertEqual "already sent" (Map.find path files.State)
        assertEqual "preserve" (Map.find unrelatedPath files.State)

    let private runNonOpenInvoiceTest () =
        let path = "outbox/invoice-reminders/INV-CLOSED"
        let files = VirtualFiles(Map.ofList [ path, "existing marker" ])
        let invoice = makeInvoice "INV-CLOSED" "Open"

        assertEqual "not-open" (queue files invoice)
        assertEqual 0 files.ReadCount
        assertEqual 0 files.WriteCount
        assertEqual "existing marker" (Map.find path files.State)

    let private runExample () =
        let files = VirtualFiles(Map.empty)
        let invoice = makeInvoice "INV-EXAMPLE" "open"
        let result =
            Execution.run true true (files :> IReminderFiles) invoice ReminderOperations.queueOnce

        assertEqual "queued" result
        assertEqual "queued" (Map.find (Invoice.reminderPath invoice.Id) files.State)
        printfn "EXAMPLE_QUEUE_REMINDER=%s" result

    let runOwnTests () =
        runMissingReminderTest ()
        runExistingQueuedMarkerTest ()
        runExistingCustomMarkerTest ()
        runNonOpenInvoiceTest ()
        printfn "OWN_TESTS_PASSED=4"
        runExample ()

    let private makePair firstId firstStatus secondId secondStatus : InvoicePair =
        { first = makeInvoice firstId firstStatus
          second = makeInvoice secondId secondStatus }

    let private runPairExistingMarkersTest () =
        let firstPath = "outbox/invoice-reminders/pair-flow-existing-first"
        let secondPath = "outbox/invoice-reminders/pair-flow-empty-second"
        let files = VirtualFiles(Map.ofList [ firstPath, "custom marker"; secondPath, "" ])
        let pair = makePair "pair-flow-existing-first" "open" "pair-flow-empty-second" "open"
        let result =
            Execution.runPair true true (files :> IReminderFiles) pair ReminderOperations.queueRemindersForPair

        assertEqual { first = "custom marker"; second = "" } result

    let private runPairFirstIneligibleSecondOpenTest () =
        let firstPath = Invoice.reminderPath (InvoiceId "pair-flow-closed-first")
        let unrelatedPath = "notes/pair-preserve"
        let files = VirtualFiles(Map.ofList [ firstPath, "existing first marker"; unrelatedPath, "" ])
        let pair = makePair "pair-flow-closed-first" "Open" "pair-flow-open-second" "open"
        let result =
            Execution.runPair true true (files :> IReminderFiles) pair ReminderOperations.queueRemindersForPair

        assertEqual { first = "not-open"; second = "queued" } result
        assertEqual 1 files.ReadCount
        assertEqual 1 files.WriteCount
        assertEqual "existing first marker" (Map.find firstPath files.State)
        assertEqual "" (Map.find unrelatedPath files.State)

    let private runPairSecondIneligibleAfterFirstQueuesTest () =
        let files = VirtualFiles(Map.empty)
        let pair = makePair "pair-flow-open-first" "open" "pair-flow-closed-second" "Open"
        let result =
            Execution.runPair true true (files :> IReminderFiles) pair ReminderOperations.queueRemindersForPair

        assertEqual { first = "queued"; second = "not-open" } result

    let private runPairTwoOpenMissingTest () =
        let files = VirtualFiles(Map.empty)
        let pair = makePair "pair-flow-missing-first" "open" "pair-flow-missing-second" "open"
        let result =
            Execution.runPair true true (files :> IReminderFiles) pair ReminderOperations.queueRemindersForPair

        assertEqual { first = "queued"; second = "queued" } result

    let private runPairDuplicateIdentifierTest () =
        let path = Invoice.reminderPath (InvoiceId "pair-flow-duplicate")
        let unrelatedPath = "notes/duplicate-preserve"
        let files = VirtualFiles(Map.ofList [ unrelatedPath, "keep" ])
        let pair = makePair "pair-flow-duplicate" "open" "pair-flow-duplicate" "open"
        let result =
            Execution.runPair true true (files :> IReminderFiles) pair ReminderOperations.queueRemindersForPair

        assertEqual { first = "queued"; second = "queued" } result
        assertEqual 3 files.ReadCount
        assertEqual 1 files.WriteCount
        assertEqual "queued" (Map.find path files.State)
        assertEqual "keep" (Map.find unrelatedPath files.State)

    let private runPairCapabilityPreflightTest () =
        let pair = makePair "pair-flow-capability-first" "open" "pair-flow-capability-second" "open"
        let assertDenied canRead canWrite =
            let files = VirtualFiles(Map.empty)
            let denied =
                try
                    Execution.runPair canRead canWrite (files :> IReminderFiles) pair ReminderOperations.queueRemindersForPair |> ignore
                    false
                with
                | :? System.InvalidOperationException as error when error.Message = "CAPABILITY_DENIED" -> true

            assertEqual true denied
            assertEqual 0 files.ReadCount
            assertEqual 0 files.WriteCount

        assertDenied false true
        assertDenied true false

    let runPairTests () =
        runPairExistingMarkersTest ()
        runPairFirstIneligibleSecondOpenTest ()
        runPairSecondIneligibleAfterFirstQueuesTest ()
        runPairTwoOpenMissingTest ()
        runPairDuplicateIdentifierTest ()
        runPairCapabilityPreflightTest ()
        printfn "PAIR_OWN_TESTS_PASSED=6"

    [<EntryPoint>]
    let main _ =
        runSeedTests ()
        runOwnTests ()
        runPairTests ()
        0
