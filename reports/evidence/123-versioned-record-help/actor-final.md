Implemented and committed `BatchBounds`, `batchBounds.valid?`, and `batch.span` through the supplied broker. Both words were accepted at **library** maturity.

The validator requires `minimum >= 1` and `maximum >= minimum`. Its four attached tests cover ordinary bounds, equal endpoints, and both invalid constructions; `batch.span` has three tests covering an ordinary span, equal endpoints, and a minimum of 1. The final `test-all` passed **7/7**. The task log records **28 test executions, 0 failures, and no errors**.

I consulted Flow/2 help for `define`, `authoring`, and `examples`. Post-commit inspection confirmed the saved type and function sources, attached test names, and current library maturity for both words. No maturity gate refused publication. Overflow behavior remains outside the task’s scope; the finite tests are evidence for the tested cases, not proof over all integer values. The broker was closed and its process exited.
