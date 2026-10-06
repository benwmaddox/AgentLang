# Conventional stateful reminder trial

This trial gives an agent a small conventional F# project and asks it to add a
tested invoice reminder operation. The preparation script writes a .NET 9
console project, then makes byte-identical actor and archived starting-project
copies. It records the source revision and SHA-256 inventories. It does not
build the project, change AgentLang, or launch an agent.

The seed has nominal `InvoiceId` and `InvoiceStatus` wrappers over `string`, an
`Invoice` record, and the pure `Invoice.reminderPath` helper. Queue behavior
belongs in `ReminderOperations.queueOnce` and receives an `IReminderFiles`
provider explicitly. `VirtualFiles` stores a `Map<string,string>` snapshot in
memory and counts lookups, reads, and writes. `Execution.run` checks both
capability flags before invoking an operation.

The virtual provider is ordinary fixture code: this project does not exercise
the operating system's filesystem sandbox or claim parity with a production
file adapter. The console self-tests use assertions, with no F# coverage gate.
The result describes one small implementation task and does not establish an
efficiency advantage or a general comparison between programming approaches.

The executed fresh-agent result and paired limitations are recorded in
[report 072](../../../reports/072-conventional-stateful-comparison.md).
