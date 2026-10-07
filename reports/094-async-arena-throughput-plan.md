# Approved async arena throughput study

Status: later research added to the roadmap; implementation and measurements
remain pending.

The user approved the recommendation to prioritize per-turn scratch arenas with
explicit retained state and bounded pending I/O. Added
`docs/ASYNC-ARENA-EVALUATION.md` and linked it from the current roadmap and memory
contract. This supersedes the unselected-candidate status in report 092 for
research priority; it does not select a proven production allocation policy.

The comparison uses whole-request arenas, identical behavior and backend, equal
enforced memory limits, and predeclared acceptable tail latency/error budgets.
It includes slow clients, cancellation, delayed I/O and large continuation-copy
workloads. Success counts completed successful requests, with rejection, timeout,
backpressure, queue growth and memory reported separately. Safety checks require
valid buffer lifetimes and bounded state before throughput testing.

No runtime changes, native memory tests or throughput measurements were made.
Agent-behavior validation retains its earlier delivery priority. Validation for
this documentation change: local link existence and `git diff --check`.
