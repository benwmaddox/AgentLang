# 247 — AOT worker-pool performance requirements

The second application performance checkpoint is now explicit:

`inbound request → one of X worker mailboxes → direct SQL/cache access → response`

It follows the single-worker baseline and uses the same native handler, data,
correctness checks, request mix and memory budget. One request remains within one
worker. Ordinary function calls and SQL/cache access do not add mailbox transfers.

Sweep worker counts and record executor-thread counts/affinity separately. Measure
sustained successful throughput, tail latency, dispatch wait, per-worker utilization,
queue depth/bytes, failures/rejections, copied bytes and process memory. Test uneven
load, slow clients and cancellation. Queues and retained responses remain bounded;
an arena cannot be reused while its response bytes are still being consumed.

Preserve mailbox single-turn execution and SQL connection/statement ownership.
Separate dispatcher contention from database contention, particularly serialized
SQLite writes. Begin with SQL and no cache; evaluate bounded, correctly invalidated
caching afterward. Report relative scaling efficiency and bottlenecks, without
promising linear scaling or fixing a worker count before measurement.

This is a requirements update, not a performance result. The native host-call and
application adapter work in [the AOT plan](../docs/CAMPFIRE-AOT-PLAN.md) remains
necessary before these checkpoints can run. Validation: documentation diff and
link/requirement inspection; no runtime code changed and no executable tests were
needed for this update.
