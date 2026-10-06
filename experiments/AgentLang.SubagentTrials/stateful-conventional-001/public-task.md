# Queue an invoice reminder exactly once

Inspect the seeded invoice types, helper, and self-test entry point. Implement
`ReminderOperations.queueOnce` in `Operations.fs` with this signature:

```fsharp
IReminderFiles -> Invoice -> string
```

Use `Invoice.reminderPath` to derive `outbox/invoice-reminders/<invoice-id>`.
When the status text is exactly `open` (case-sensitive), check whether the
path exists. If it does, read and return the exact contents without changing
them. If it does not, write `queued` and return `queued`. For every other
status, return `not-open` without calling `Exists`, `Read`, or `Write`.

Use the explicit provider passed to the operation. The supplied
`Execution.run` wrapper preflights read and write permission before invoking
an operation; if either permission is missing, it raises
`InvalidOperationException("CAPABILITY_DENIED")` before any provider call.
Do not modify `Domain.fs`, `StatefulPilot.fsproj`, or the bytes between the
`<immutable-seed-tests>` markers in `SelfTests.fs`.

Add a concise XML documentation comment for `queueOnce`. Add meaningful
assertion-based self-tests to `SelfTests.fs` for a missing reminder, an
existing `queued` reminder, an existing reminder with different marker text,
and a non-open invoice with unchanged state and zero provider calls. Run all
four from `runOwnTests()` and print `OWN_TESTS_PASSED=4` only after their
assertions pass. Also add a runnable example function to `SelfTests.fs`, invoke
it from `runOwnTests()`, and print `EXAMPLE_QUEUE_REMINDER=queued` when its
open-invoice example succeeds. The seeded console entry point already runs the
retained helper tests and then `runOwnTests()`.

Run the supplied broker's `validate` operation and preserve the two seeded
path-helper tests. The broker selects `StatefulPilot.fsproj` for local execution.
