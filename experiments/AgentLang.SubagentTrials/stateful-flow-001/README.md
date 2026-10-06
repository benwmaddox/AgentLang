# Stateful virtual-file reminder trial

This trial asks whether a fresh AI coding agent can discover the seeded invoice
vocabulary and implement a tested operation with stateful, simulated effects.
The preparation script builds the seed through ordinary Flow definitions,
tests, and commits, then gives the actor a byte-identical project copy. It does
not change the runtime or launch an agent.

The seed has nominal `InvoiceId` and `InvoiceStatus` wrappers over `String`, an
`Invoice` record, and the tested pure library word
`invoice.reminder-path(InvoiceId) -> String`. The wrappers are distinct types
but have no validation rules. The queueing task requires the status to equal
`open` exactly and uses a fixed virtual path derived from the invoice ID.

All filesystem behavior in this trial uses the runtime's project-scoped virtual
file provider, with `fs.read` and `fs.write` enabled for the CLI. It does not
read or write host files, send real email, or demonstrate parity with a full
business application. The agent's new library word must provide its own tests
for a missing reminder, an existing `queued` reminder, an existing arbitrary
marker, and a non-open invoice, and must pass its own instruction and branch
coverage gate before durable commit.

The coordinator's independent acceptance checks own the prelaunch pin, prefix
state, per-case virtual-file behavior, no-effect behavior for non-open invoices,
durable reload, and preservation audit. Results from this small trial describe
one stateful language task; they do not establish token or latency improvements
or prove the full PRD's benchmark claims.
