The pair-reminder function is published as library revision 2. It processes each invoice independently in order through the existing once-only helper, while retaining the `InvoicePair` → `ReminderPairResult` contract and both filesystem effects.

The focused suite passed (6/6), and the full project suite passed (12/12). Regression cases cover an ineligible first invoice with an eligible second, duplicate IDs observing the first write, and repeated runs preserving custom and empty markers. The broker reports complete coverage.

The broker was closed, and polling its session returned "Unknown process id," indicating it had exited. I missed the required final `task.commit` before closing it, so task-level finalization remains unconfirmed. Two attempts also failed without changing source: the initial test syntax used `= >` instead of `=>`, and a `diff` request omitted required revision fields.
