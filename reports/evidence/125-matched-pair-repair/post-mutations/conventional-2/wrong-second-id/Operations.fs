namespace AgentLang.StatefulPilot

module ReminderOperations =
    /// Queues a reminder once, preserving any contents already stored at its path.
    let queueOnce (files: IReminderFiles) (invoice: Invoice) : string =
        match invoice.Status with
        | InvoiceStatus "open" ->
            let path = Invoice.reminderPath invoice.Id

            if files.Exists path then
                files.Read path
            else
                files.Write(path, "queued")
                "queued"
        | _ -> "not-open"

    /// Processes both invoices independently in order, preserving existing reminder contents.
    let queueRemindersForPair (files: IReminderFiles) (pair: InvoicePair) : ReminderPairResult =
        let firstResult = queueOnce files pair.first
        // Mutant: use the first invoice identifier for the second reminder path.
        let secondResult = queueOnce files { pair.second with Id = pair.first.Id }

        { first = firstResult
          second = secondResult }
