namespace AgentLang.StatefulPilot

module ReminderOperations =
    /// Queue an open invoice once, preserving an existing reminder marker.
    let queueOnce (files: IReminderFiles) (invoice: Invoice) : string =
        let (InvoiceStatus status) = invoice.Status
        if status = "open" then
            let path = Invoice.reminderPath invoice.Id
            if files.Exists path then files.Read path
            else
                files.Write(path, "queued")
                "queued"
        else "not-open"