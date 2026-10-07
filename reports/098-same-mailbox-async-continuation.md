# Same-mailbox async continuation candidate

Status: later research refinement, not implemented async language semantics.

The user proposed putting data surviving async processing into mailbox state,
returning the stack arena to a pool, and making another arena available on
completion. They clarified that continuation processing should use the same
mailbox rather than requiring a new one. Recorded this in the async evaluation
contract and roadmap, preserving the static-state/temporary-stack distinction.

Required safety work includes bounded typed continuation storage, whole-value
promotion, retained I/O buffers, pending-operation identity and valid resumption
after cancellation or code replacement. A fresh logical arena can reuse physical
storage without making old references valid. Pool return and idle OS release are
different operations. Scheduling unrelated messages during suspension remains
open; single-thread execution does not settle that ordering policy.

No interpreter, native runtime, mailbox scheduler or allocator was changed.
This does not alter the near-term agent-validation priority. Validation:
documentation links and local diff checks; no runtime result is claimed.
