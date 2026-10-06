namespace AgentLang.StatefulPilot

open System

/// A nominal invoice identifier backed by text.
type InvoiceId = InvoiceId of string

/// A nominal invoice status backed by text.
type InvoiceStatus = InvoiceStatus of string

/// The invoice value used by the reminder operation.
type Invoice =
    { Id: InvoiceId
      Status: InvoiceStatus }

/// Helpers for invoice values.
module Invoice =
    /// Builds the relative virtual path used for an invoice reminder.
    let reminderPath (InvoiceId id) : string =
        "outbox/invoice-reminders/" + id

/// The explicit file provider used by reminder operations.
type IReminderFiles =
    abstract Exists: path: string -> bool
    abstract Read: path: string -> string
    abstract Write: path: string * contents: string -> unit

/// An in-memory file provider with inspectable state and operation counts.
type VirtualFiles(initial: Map<string, string>) =
    let mutable state = initial
    let mutable readCount = 0
    let mutable writeCount = 0

    /// A persistent snapshot of the current virtual files.
    member _.State: Map<string, string> = state

    /// The number of existence and read operations since the last reset.
    member _.ReadCount: int = readCount

    /// The number of write operations since the last reset.
    member _.WriteCount: int = writeCount

    /// Resets operation counts without changing virtual file state.
    member _.ResetCounts() =
        readCount <- 0
        writeCount <- 0

    interface IReminderFiles with
        member _.Exists path =
            readCount <- readCount + 1
            Map.containsKey path state

        member _.Read path =
            readCount <- readCount + 1
            Map.find path state

        member _.Write(path, contents) =
            writeCount <- writeCount + 1
            state <- Map.add path contents state

/// Runs an operation only when both virtual file capabilities are granted.
module Execution =
    /// Preflights read and write access before invoking the supplied operation.
    let run
        (allowRead: bool)
        (allowWrite: bool)
        (files: IReminderFiles)
        (invoice: Invoice)
        (operation: IReminderFiles -> Invoice -> string)
        : string =
        if not (allowRead && allowWrite) then
            raise (InvalidOperationException("CAPABILITY_DENIED"))

        operation files invoice