# Native Campfire delivery

Updated 2026-10-12. Primary deliverable: complete Campfire implemented in the
language, compiled through LLVM AOT, with comparable application performance
measurements. The interpreter is a semantic and test reference. A release must
run without the interpreter, compiler, F# assemblies or .NET runtime.

## Current boundary

The application currently executes through the interpreter and .NET adapters.
Its tested SQLite, cookie, browser and HTTP behavior is useful reference evidence,
but there is no native Campfire endpoint or measured AOT Campfire throughput.
Report 140's native mailbox load fixture is not a Campfire benchmark; approximately
500 completed requests/sec was an offered-load point with simulated I/O, not
maximum application throughput.

User clarification, 2026-10-12: optimize inbound request to a single worker
mailbox, direct SQL/cache interaction, and returning the response. Ordinary
function calls stay within that turn; do not introduce per-function or SQL/cache
mailbox hops. Start with real SQL and caching disabled. Reuse a connection and
prepared statements owned by the worker's thread where correctness permits.
Retain response storage until the socket finishes consuming it, then reset or
pool the arena. Async suspension must preserve only explicit required state or
deliberately retain the arena; compare the two policies rather than assuming a win.

The first request benchmark records dispatch/queue wait, SQL time, rendering,
response-write completion and arena reuse, plus sustained whole-request throughput,
tail latency and memory. Compare direct execution with one worker dispatch using
the same native handler. Cache evaluation follows the SQL baseline, with bounded
storage and explicit invalidation/authentication checks. Multiple stage transfers
are optional diagnostic comparisons, not the intended application architecture.

The second performance checkpoint scales the same path to a pool:
**inbound request → one of X worker mailboxes → direct SQL/cache access → response**.
Each request still runs in one worker; the pool is not a pipeline of mailbox hops.
Sweep worker counts from one through available CPU capacity and beyond it where
useful, recording mailbox count, executor-thread count and affinity separately.
More mailboxes alone do not establish parallel execution.

Compare the same correctness-checked workload, data, request mix and memory budget.
Measure sustained successful requests/sec, p50/p95/p99, dispatch/queue wait,
per-worker utilization and queue depth, rejections, copied bytes and whole-process
memory. Include uneven load, slow clients and cancellation. Bound queued requests
and bytes; retain response ownership until writes complete. Check that dispatch
does not serialize otherwise independent workers behind a global execution lock.

Preserve one active turn per mailbox and worker/thread ownership of SQL connections
and prepared statements. Measure database contention separately, including reads
and writes; SQLite write serialization must not be mistaken for dispatcher cost.
Evaluate cache-disabled SQL first, then bounded caching with correct invalidation
and authentication. Report speedup and efficiency relative to the one-worker
baseline, the saturation point and the cause of any regression. Do not assume
linear scaling or choose a production worker count before measuring.

Use `OwningStackAot.fs` and the owning arena runtime as the native base. They
already lower strings, nominal strings, records, enums, Option/Result and matches.
The older scalar backend cannot represent the application's request/response
types. Default owning compilation still rejects effects and host calls. An explicit
native profile now lowers the exact `campfire.rooms.find-for-user` contract to a
statically linked adapter, carrying worker-owned provider state per execution.
The synthetic boundary fixture remains a test. A native SQLite adapter now executes
the room lookup through the same typed AOT entry, with a worker-owned connection
and reusable prepared statement. This is not yet an HTTP endpoint.
Broader host contracts and list support remain required. The fixed
initialize/begin/resume mailbox exports are not a general application host interface.

## Delivery order

1. Export a compiler-minted verified entry body from a committed Flow dictionary.
   Reuse the existing binding's exact identity, revision, source and signature
   checks. [Verified entry export](../reports/246-verified-entry-ir-export.md) now
   passes focused native/runtime conformance. Export is build-time only; typed
   host-call admission remains the next blocker. Keep semantic IR authoritative.
2. Add typed native host calls with a fixed build-time contract and effect allowlist.
   Start with the existing `campfire.rooms.find-for-user` nominal/record/Option/Result
   signature. Pin required operation identities, signatures, effects, type layout
   and runtime profile in the module descriptor. Missing or mismatched providers
   must fail before invocation. Default pure compilation remains restrictive.
   [The first native host profile](../reports/248-typed-native-room-host.md) pins
   the exact nominal schema and statically linked adapter source. It writes bounded
   output directly into the arena, validates the serialized value, and commits
   without a payload copy; failed output reservations rewind. A missing adapter
   symbol fails at link time. A general provider descriptor/handshake is still
   future work; this profile does not claim one. This compiler milestone is not an
   application endpoint. Expand native contracts to the shared room-path closure,
   including conditional session-refresh writes, before measuring that endpoint.
3. Add the list layout/instructions and `int.to-string` required by room display.
   Emit a general owning module descriptor and entry ABI. Audit every affected
   version, offset, capacity, layout fixture and deterministic oracle. Preserve
   conservative compiler-proven rewinds; do not add automatic compaction.
4. Compile a complete GET/HEAD room path. A separately bound Flow entry may share
   existing authentication and room-policy helpers while excluding unrelated
   write/login routes from its static closure. Prove parity with the generic
   dispatcher for that endpoint; avoid a second native implementation of policy.
5. Supply narrow native SQLite, cookie/crypto, browser, rendering and HTTP adapters.
   Use established native libraries/OS facilities, parameterized SQL and bounded
   input/output. Preserve existing SQLite data and Rails-compatible cookies.
   Produce an executable/native dependency manifest and verify the release loads
   without .NET. Windows clang/lld and CNG import libraries are available; the
   room adapter uses the pinned SQLitePCLRaw.lib.e_sqlite3 2.1.12 native library
   (SQLite 3.53.3), with version/options and package-byte verification. Standalone
   C smoke tests execute the compiled room entry without .NET at O0/O2. See
   [the native SQLite milestone](../reports/249-native-sqlite-room-adapter.md).
   Remaining operation contracts and HTTP/cookie/browser adapters are still required.
6. Run real loopback HTTP parity, then measure the native room endpoint. Continue
   native migration through login, messages, logout, setup, live delivery and the
   remaining application inventory. A native room page is the first performance
   checkpoint, not the completion definition.

## Value and effect boundary

Native callbacks take typed inputs borrowed only for the duration of the call.
Outputs must become arena-owned values before language execution resumes. Prefer
direct construction through a bounded reserve/validate/commit interface; copy
temporary provider data only where its lifetime requires an ownership transfer.
Measure those copied bytes. Do not return retained SQLite pointers, references to
released callback buffers or arbitrary pointers into older stack data.

Validate output type, tags, extent, capacities and layout. Distinguish host-boundary
failure from a valid language `Result.error`. Keep temporary allocation rollback
separate from external database effects: a failed response cannot undo a committed
write by rewinding an arena. Trusted adapters must not implement opaque route or
authorization decisions. No request-selected imports or arbitrary library calls.

## Acceptance before performance claims

Native conformance covers O0/O2, allowed/denied effects, unknown and mismatched
host contracts, malformed/capacity-failing outputs, ownership and rewind behavior,
and interpreter parity for the actual typed Campfire signature. Existing pure and
mailbox native gates must continue to pass.

The room HTTP corpus must cover signed/tampered/expired cookies, session refresh,
missing users and bot keys, browser gating, Origin/Fetch-Site policy, member and
nonmember rooms, unavailable storage, newest-message ordering, HTML escaping,
last-room cookies, HEAD, malformed/oversized input and bounded output failures.
Compare status, body and cookie/header semantics; compare decoded encrypted state
when nonces differ. Use the existing schema and seeded SQLite fixtures.

## Performance comparison

Reproduce the reference workload's endpoint, data volume, response content,
authentication, cache behavior, keepalive/compression, concurrency and warmup.
Record hardware, OS, compiler/profile, process configuration and native dependencies.
Report completed correct requests/sec, failures, p50/p95/p99, whole-process memory,
arena capacity/peak and copied bytes. Use sustained runs and repeat selected points.
Do not use an offered-load point as a saturation limit.

The pinned Campfire README reports room-page rates of 4,101/sec for Rails and
106,494/sec for Rust. These published rates are comparison targets, not portable
baselines on this workstation. Prefer rerunning the same harness locally against
reference implementations; otherwise label differences and avoid a speedup claim.
Native kernels and interpreter timings remain separate evidence. Save raw results,
methodology and a report alongside every completed milestone, validate locally,
and merge/push the tested source and report together.

The full scope remains the [177-row route inventory](campfire-route-inventory.json),
plus live protocols, assets, stored files, jobs, data migration and deployment.
