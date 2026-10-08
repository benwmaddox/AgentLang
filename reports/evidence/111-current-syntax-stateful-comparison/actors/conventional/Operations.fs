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