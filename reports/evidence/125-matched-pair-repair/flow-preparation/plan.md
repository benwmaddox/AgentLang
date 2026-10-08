# Flow fixture preparation plan

Use the report 111 `starts/flow` project as the reset seed and `actors/flow` as the retained seed. Copy the frozen report 121 Debug runtime from `record-validation-help-adoption-002`, pin it with its runtime manifest, and use the existing v2 trial broker.

Define the same Flow/2 records and inline pair function in both seeds: `InvoicePair { first: Invoice; second: Invoice }`, `ReminderPairResult { first: String; second: String }`, and `invoice.queue-reminders-for-pair(pair: InvoicePair) -> ReminderPairResult`. Keep the inline per-invoice open check and filesystem behavior, including the intentional early return when the first invoice is not open. Attach focused passing tests that encode this old behavior and cover filesystem read/write branches; retain all inherited tests and source.

Acceptance: both copied projects load in a fresh process under the pinned runtime; each target has four or more passing attached tests and library maturity; the target source is byte-identical between seeds; the retained copy contains the report 111 `invoice.queue-reminder-once` word and its tests, while reset has no such word; seeded types/helper provenance remain intact. Keep all authored artifacts within this preparation directory and its authorized shared `runtime` and `starts/retained`, `starts/reset` directories. Do not build runtime or touch historical inputs.

Validation uses the existing broker JSONL protocol: `help` for define/test/commit requirements, `define` with Flow/2 source, `test`/`test-all`, `commit`, then a fresh broker reload followed by `source`, `tests`, and `describe`/maturity inspection. Record request/response logs and compare saved target source bytes plus copied runtime hashes.
