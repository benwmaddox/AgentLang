# Queue an invoice reminder exactly once

Inspect the existing invoice types and words, then add a durable library word
with this signature:

    invoice.queue-reminder-once : Invoice -> String

Use the existing `invoice.reminder-path` word to derive the fixed virtual path
for the invoice ID. If the nominal invoice status's underlying text is exactly
`open` (case-sensitive), check whether that path already exists. If it exists,
read and return its exact contents without changing it. If it does not exist,
write `queued` there and return `queued`. For every other status, return
`not-open` without reading or writing any virtual file.

`InvoiceId` and `InvoiceStatus` are nominal wrappers without validation; don't
add status normalization or other validation. The path is
`outbox/invoice-reminders/<invoice-id>`. Files belong to the runtime's
project-scoped virtual provider; this task does not access host files or deliver
email.

Add concise documentation and at least one first-class example for the new
word. Attach meaningful tests for a newly queued open invoice, an existing
`queued` entry, an existing entry containing different marker text, and a
non-open invoice. Cover every instruction and both outcomes of each branch in
the new word's own tests, then commit it durably as a library word. Preserve the
seeded types, helper, and their tests.
