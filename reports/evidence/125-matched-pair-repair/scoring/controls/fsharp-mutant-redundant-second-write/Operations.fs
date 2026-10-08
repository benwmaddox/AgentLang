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
        let firstResult =
            match pair.first.Status with
            | InvoiceStatus "open" ->
                let path = Invoice.reminderPath pair.first.Id

                if files.Exists path then
                    files.Read path
                else
                    files.Write(path, "queued")
                    "queued"
            | _ -> "not-open"

        let secondResult =
            match pair.second.Status with
            | InvoiceStatus "open" ->
                let path = Invoice.reminderPath pair.second.Id

                if files.Exists path then
                    let contents = files.Read path
                    files.Write(path, contents)
                    contents
                else
                    files.Write(path, "queued")
                    "queued"
            | _ -> "not-open"

        { first = firstResult
          second = secondResult }