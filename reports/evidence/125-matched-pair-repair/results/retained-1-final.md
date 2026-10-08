Repaired and committed `invoice.queue-reminders-for-pair` in the `retained-1` trial project at library maturity. It now processes each invoice sequentially through the existing `invoice.queue-reminder-once` helper, preserving the public types and read/write effects.

Updated the defect-encoding expectation and added regressions for exact status matching, zero provider calls when both invoices are ineligible, duplicate IDs, repeated workflows, and preserved empty or custom markers.

Validation passed: pair tests **7/7**, all dictionary tests **13/13**, and the attached example **1/1**. The broker's task commit reported 31 tests run and 0 failures. The broker closed and exited successfully.

The initial sandboxed read of the assigned prompt failed with an ACL setup error; I retried using the explicitly authorized scoped host execution. No other launch attempts or inspected files.
