# SQLite transaction and concurrency policy

Updated 2026-10-12. Strict correctness and durability are the initial baseline.
This policy applies to the native Campfire host. Milestone 252 validates the
room-read adapter's settings, snapshots, contention and cleanup. Milestone 253
adds native room HTTP and one/four-worker read measurements. Broader concurrent
write, deadline and checkpoint-pressure tests remain required; the complete
native application migration is unfinished.

## Connections and isolation

Each worker owns one connection and its prepared statements. Calls are serialized
on that worker's initializing thread. Private caches and `read_uncommitted=0`
prevent dirty reads; no connection or active statement is shared between workers.
NOMUTEX is permitted only while that ownership rule is enforced. Connection reuse
is not permission to interleave independent requests inside a transaction.
The SQLite build and startup configuration must support multi-thread use;
NOMUTEX does not make a single-thread SQLite build safe across workers. A worker
must finish/reset its active SELECT before mutating the same connection's queried
data: SQLite does not define reliable cursor visibility for that interleaving.
Copy required result values into the request's owned arena before resetting a
statement; SQLite column pointers must never escape into retained mailbox state.
[SQLite isolation](https://www.sqlite.org/isolation.html),
[threading modes](https://www.sqlite.org/threadsafe.html), and
[shared-cache guidance](https://www.sqlite.org/sharedcache.html).

## Storage and durability

The service baseline uses WAL on local storage. Provision and verify WAL before
starting the worker pool; workers must not race to change journal mode. Configure
and verify `synchronous=FULL`, foreign-key enforcement, and dirty reads disabled
on every connection. Do not use NORMAL or OFF to improve the reported strict-mode
benchmark. FULL provides WAL durability; EXTRA is appropriate if a future explicit
rollback-journal compatibility mode requires strict durability. The modes must be
reported separately. [SQLite pragma documentation](https://www.sqlite.org/pragma.html#pragma_synchronous).

WAL allows readers and a writer to proceed concurrently, but only one writer can
hold the write transaction at a time. More worker connections do not create more
simultaneous writers. Network filesystems are outside this WAL deployment profile.
Keep read snapshots short: an old reader can hold back checkpoint progress. Finish
SQL work before awaiting network output or a slow client. Initially retain SQLite's
automatic checkpoint policy, record its actual threshold, and measure WAL growth
and checkpoint latency before adding a dedicated checkpoint controller.
[SQLite WAL guidance](https://www.sqlite.org/wal.html).

All connections participating in concurrent WAL workloads must use a build with
the WAL-reset corruption fix. SQLite documents the fix in 3.51.3 and later, with
specific earlier backports. Our native dependency reports 3.53.3; validate the
actual loaded library, not only the package name. Interpreter/reference providers
must also be audited before they participate in these concurrency tests.
[SQLite WAL-reset fix](https://www.sqlite.org/wal.html#walreset).

## Transaction boundaries and failures

Use a deferred transaction for multiple reads requiring one stable snapshot. A
read-only operation must never acquire a write reservation merely to take that
snapshot. For a read-modify-write operation, acquire `BEGIN IMMEDIATE` before its
first read. Avoid upgrading an old read snapshot after another writer commits.
If a snapshot must be retried, end it and repeat the entire decision from fresh
reads. Independent operations remain independent transactions; this does not
make an entire HTTP request atomic. Future operations needing atomic authorization
and mutation must explicitly place both inside the same transaction.
[SQLite transaction semantics](https://www.sqlite.org/lang_transaction.html) and
[snapshot isolation](https://www.sqlite.org/isolation.html).

Contention waits have a finite deadline. A busy timeout is not a guarantee that
SQLite will wait on every lock conflict, and cannot repair a stale snapshot.
Do not replay only the final update, retry arbitrary I/O failures, or automatically
replay a callback that may already have committed. The current bounded timeout
returns a typed database failure when exhausted; later safe whole-transaction
retries require confirmed rollback and an explicit retry/deadline policy.
[SQLite busy-handler rules](https://www.sqlite.org/c3ref/busy_handler.html).

A failed COMMIT may leave the transaction active. Reset/drain statements, inspect
`sqlite3_get_autocommit`, and roll back an unfinished transaction on failure.
Verify that cleanup restored autocommit before reusing the connection. If cleanup
fails, reject later operations on that context and allow owner-thread destruction;
do not silently hand contaminated state to the next request. Preserve the original
failure before cleanup changes SQLite's error state.
[COMMIT failure semantics](https://www.sqlite.org/lang_transaction.html) and
[autocommit inspection](https://www.sqlite.org/c3ref/get_autocommit.html).

## Accommodation in the language and worker host

Keep transaction ownership explicit at the trusted SQL boundary. The current
profile's callbacks each finish their transaction before returning; composing
several such callbacks does not provide a shared snapshot or atomic mutation.
When a Campfire operation requires several dependent reads and writes to be
atomic, provide one narrowly typed transactional operation, or an explicit
transaction scope whose operations share the worker's connection. Do not expose
raw connection handles to language values. Reject nested BEGIN scopes; add
savepoints only if a concrete operation needs nested rollback. SQLite BEGIN
transactions do not nest.
[SQLite transaction semantics](https://www.sqlite.org/lang_transaction.html).

The worker host must accommodate a request deadline shorter than the initial
five-second SQLite busy timeout. Bound lock waiting by the remaining deadline,
and finish rollback/reset before completing cancellation or recycling a worker.
If later write-heavy measurements justify admission control, use a bounded
permit around write transactions while SQL still executes on the selected
worker. Admission must not hold a read snapshot while waiting for a write permit.
This is a project design recommendation, not a requirement imposed by SQLite.

Do not depend on experimental multi-writer extensions for the baseline. Treat
one writer as a per-database capacity constraint, and measure contention with
multiple workers before increasing the pool. Keep maintenance/checkpoint
coordination separate from application transaction ownership. A blocked
checkpoint must not silently relax durability or retain a request's arena.

## Required evidence

The local gate must verify effective connection settings and the loaded SQLite
version; stable old snapshots across another connection's commit; a new reader
seeing committed state; a competing writer exhausting its bounded wait without
partial updates; cleanup and subsequent reuse; and rollback-failure rejection.
Native request performance must report transaction mode, durability, worker count,
contention failures, checkpoint behavior and tail latency. A later bounded writer
admission mechanism may serialize write attempts while execution stays in the
selected worker; a separate SQL mailbox is not required by this policy.

## Strict multi-worker integration requirements

The implementation must accommodate these rules before expanding the native
profile to application writes. This is a host/provider contract, not new language
syntax or a requirement to implement a general database framework first.

| Concern | Required accommodation |
| --- | --- |
| Atomic decisions | Authorization, invariant checks and dependent writes belong in one explicit transaction where correctness depends on observing the same state. Separate callbacks are not implicitly atomic. |
| Transaction lifetime | No network wait, response streaming or unrelated effect inside an open transaction. Complete statements and commit/rollback before returning database-owned storage or releasing a worker. |
| Writer contention | Acquire IMMEDIATE before dependent reads. Initially let SQLite serialize writers with a bounded wait; keep room for a bounded writer-admission permit on the same worker if measured contention warrants it. |
| Deadlines and cancellation | Host-controlled remaining time must bound lock waits. The current fixed 5,000 ms adapter timeout is not request cancellation. A future custom busy handler must be non-reentrant; setting busy_timeout would replace it. Cancellation must complete cleanup before reuse. |
| Failure diagnostics | Preserve the primary and extended SQLite error codes in host diagnostics, together with operation/transaction stage and cleanup outcome. Distinguish ordinary contention, stale-snapshot failure, constraints, cancellation, disk-full and I/O failures even if the current public Result maps several to Unavailable. |
| Checkpoint pressure | Record WAL bytes, log/checkpointed frames, incomplete/busy checkpoints and latency. Retain the initial 1,000-page automatic threshold until evidence justifies changing it. Maintenance must use its own owned connection if moved off request workers. |
| Resource bounds | Bound worker count, waiting write attempts and per-connection cache budgets. More connections increase memory and possible contention; they do not increase simultaneous writer capacity. |

`synchronous=FULL` durability and `SQLITE_CHECKPOINT_FULL` are different controls.
Do not force a blocking FULL/RESTART/TRUNCATE checkpoint on every request. SQLite's
PASSIVE checkpoint does not wait for readers/writers and may finish incompletely;
even a successful return is not proof that the WAL is drained. If we introduce
explicit maintenance, prefer nonblocking progress during traffic and reserve
blocking reset/truncation for a bounded maintenance window. Never delete live
WAL/SHM files to control their size.
[Checkpoint API](https://www.sqlite.org/c3ref/wal_checkpoint_v2.html) and
[WAL lifecycle](https://www.sqlite.org/wal.html).

Extend the integration corpus as each capability lands: overlapping readers with
a committing writer; competing writers with exact final-state checks; stale read
snapshot upgrade rejection; deadline expiration followed by clean reuse; long
readers delaying checkpoints and progress after they finish; and failed
commit/rollback handling. Before claiming production durability, add separate
process-kill/reopen integrity and atomicity tests. Process termination tests do not
establish power-loss durability. Do not infer these results from the existing
single-endpoint throughput test or from FULL alone.
