namespace AgentLang.StatefulPilot

module ReminderOperations =
    /// Queues an open invoice reminder once, preserving existing contents.
    let queueOnce (files: IReminderFiles) (invoice: Invoice) : string =
        let status =
            match invoice.Status with
            | InvoiceStatus text -> text

        if status <> "open" then
            "not-open"
        else
            let path = Invoice.reminderPath invoice.Id
            if files.Exists path then
                files.Read path
            else
                files.Write(path, "queued")
                "queued"