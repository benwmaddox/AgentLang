# 253 — Native Campfire HTTP integration

Status: validated native room-read HTTP checkpoint, 2026-10-12. Complete native
Campfire migration remains in progress.

The host and sixteen helper sources/headers are now under
`applications/campfire/Native/http`; their source hashes match the reviewed
private prototypes, with the expiry helper added after a detected header defect.
Eight helpers compile under strict Clang warnings at O0 and
O2. The measurement client is under `experiments/AgentLang.Campfire.HttpMeasure`;
its fresh Release build and histogram self-test pass. The standalone receipt-driven
builder passes at O0/O2, including rejection of failed/missing receipts, unstable
or changed sources, missing SQLite pins, changed artifacts and an incompatible
runtime profile. Fresh runtime and room-read validation pass; the final O0/O2
hosts match twenty HTTP metadata/body cases against the warmed control. Six
paired cookie/header cases pass 291 assertions per optimization. Corrected
one-worker and four-worker timing is recorded below, with zero response failures.

This milestone connects the accepted [native room-read entry](252-native-room-read-host.md)
to a standalone Windows HTTP Server API v2 host. The first endpoint is room
GET/HEAD; the complete Campfire migration remains the overall deliverable.

## Architecture and acceptance

One HTTP.sys queue supplies a fixed set of worker threads. Each worker constructs
the typed request, calls the LLVM AOT entry through its own SQLite connection,
renders, finalizes cookies and sends the response on that thread. No separate SQL
queue or per-function mailbox transfer is introduced. Execution and response
arenas remain owned until synchronous sending completes; requests never share
mutable crypto scratch. Queue limits and HTTP.sys kernel buffering are reported.

Initially the host uses the accepted milestone-252 artifact after checking its
receipt, source pins, binary hash and exact compiler-minted profile/schema. The
build/test driver may use .NET; the release host and request execution must not.
The final integration build must remain reproducible from committed sources.

The old compiled module and the response-cookie prototype have distinct opaque
crypto context layouts. Initialize each through its own module/API and never
interchange their pointers. Any later consolidation needs fresh native conformance.

Capacity failures must not trigger blind replay of the language entry: session
refresh may already have committed. Rendering can measure/grow its output buffer
without rerunning application policy. SQLite transactions finish before sending.

Loopback acceptance requires status/body/header parity for authenticated Unicode
pages, HEAD, missing/invalid authentication, refresh and last-room cookies,
encrypted remember-return-URL/flash mutations, relevant duplicate cookies,
Host/header limits, escaped hostile text, repeated/concurrent worker reuse and
shutdown cleanup. Randomized encrypted cookies are compared by decoded state.

Only then measure completed correct requests/sec and p50/p95/p99 using keepalive
clients, with one and multiple workers. Report hardware, dataset/response size,
SQLite FULL/WAL, failures, memory/arena capacities, checked runtime instrumentation,
HTTP.sys caching/queue settings and synchronous-send limits. Published Campfire
numbers remain contextual comparisons until the workloads are equivalent.

## Current evidence

Private helpers are being developed and reviewed independently before integration.

| Component | Evidence so far |
| --- | --- |
| AOT policy and providers | Milestone 252 shipped; 234 integrated assertions, O0/O2 parity and standalone no-CLR execution |
| Browser normalizer | 402 direct F# reference records at O0/O2; capacity/alignment/canary checks; root rerun passed; Unicode claim limited to documented mappings/corpus |
| HTML encoder | Direct HtmlEncoder.Default parity, including all ASCII/BMP code units, supplementary/malformed UTF-16 and capacity boundaries, O0/O2; root rerun passed |
| Request encoder | Saved 360-byte fixture plus 22 typed requests matching the freshly built compiler ABI encoder at O0/O2; 423 checks; canonical room GET/HEAD shape only; broader HTTP coverage pending |
| Response cookie wire encoding | Native ASCII crypto/numeric output escaping matches Rack for all ASCII at O0/O2, with capacity/limit checks; incoming decoding separate |
| Incoming cookie decoder | 65,840 direct Rack reference cases at O0/O2, including exhaustive one/two-byte percent-decoded values, malformed escapes, replacement boundaries and capacity/alignment checks; root rerun passed |
| Response crypto and state mutation | O0/O2 native/.NET signing/encryption/decryption interoperability, fixed return-URL/flash mutation, property preservation, capacity failures and cross-context rejection pass; root rerun passed; endpoint integration pending |
| Response decoder/renderer | 615 assertions per O0/O2 build against ResponseRenderer.render; exact body/metadata, forty-row Unicode page, malformed ABI/alignment, INT64_MIN and capacity checks; root rerun passed |
| HTTP.sys host and measurements | Authenticated populated GET/HEAD pass; 20 HTTP cases match the local interpreted control; initial short samples expose CPU-bound native overhead; investigation remains in progress |

No prototype helper is claimed to be a complete native HTTP endpoint. Final
reports, source pins and acceptance/measurement evidence will be sealed before
this milestone is committed and pushed.

The standalone HTTP.sys startup probe now passes at O0 and O2 in the normal host
context: exact `http://127.0.0.1:13773/` prefix bind/remove, queue length 128
readback, session/group setup and all shutdown statuses succeed. No URL ACL was
changed. The low-integrity sandbox rejects initialization; host-side execution is
required for this local test. The first host probe also identified an unsupported
binding-property query (Win32 87); the corrected probe records setter success and
actual URL registration. This establishes startup only, not HTTP response parity.

A private shared HTTP measurement client builds in Release and passes histogram
boundary/quantile checks. It uses bounded loopback HTTP/1.1 keepalive, verifies the
complete response body SHA256 on every request, includes final in-flight drain in
elapsed time, records failures and correct-response latency, and samples server
working set/private bytes every 100 ms. The same client is intended for native
and reference runs. No endpoint timing is claimed until real HTTP validation.
Real loopback client tests now verify successful body counts/bytes/latency and
complete rejection of wrong bodies and wrong statuses. The fixture process
stopped cleanly. Its timings validate the harness only, not Campfire performance;
local socket use requires host execution outside this sandbox.

Independent host review identified cookie-limit status parity, unexpected worker
termination, query-key decoding and credential-buffer cleanup issues. Corrections
are in progress before acceptance. A fresh standalone ASP.NET query reference
pins 32 cases without application bootstrap: decoded/case-insensitive keys and
duplicate aggregation, plus partial percent decoding around malformed escapes.
Invalid escaped UTF-8 remains escaped. These query semantics differ from the
verified cookie decoder; the host must not reuse it indiscriminately.
Build review also identified duplicate copied pure-provider exports if both the
old adapter/pure sources and the response codec were linked into the executable.
The intended boundary is unchanged: old SQL/pure lifecycle and entry calls come
from the pinned 252 DLL; the separate response context belongs to the executable.
The host build is being corrected to preserve that boundary before execution.
Strict standalone O0/O2 host builds now succeed. The first real launch failed
closed before binding or running SQL because the embedded expected manifest hash
was stale, while copied artifact hashes matched the accepted receipt. The host
pins are being corrected and rebuilt; no endpoint success is inferred from build
success. A disposable copied database with a freshly seeded synthetic session is
ready for the authenticated smoke test.
After fixing the manifest pin, stage-specific diagnostics caught a second manual
byte-array error in the expected entry-module hash. Both launches exited before
binding or running SQL. The build will generate expected hash arrays from the
verified artifact digests rather than retaining manually transcribed bytes.

The local reference server will be labelled **.NET/Kestrel + interpreted language
policy**, using the same disposable database, secret and client request corpus.
It is a semantic/timing control, not conventional compiled F# performance. Native
HEAD will advertise GET-equivalent Content-Length; the current reference may omit
that optional header. Compare application metadata and record HTTP framing/server
header differences rather than silently modifying the reference to hide them.
The existing interpreted providers disable connection pooling and open a SQLite
connection per operation; the native adapter reuses a worker-owned connection
and prepared statements. Any local timing difference therefore includes provider
and transport architecture, not only interpreted versus AOT execution. Preserve
that reference behavior and disclose the difference; verify strict database
settings and the loaded native SQLite version for both runs.

Decoder testing found that Win32 UTF-8 replacement grouping differs from the
reference for overlong sequences. The helper therefore uses bounded .NET-compatible
conversion rather than assuming OS conversion is interchangeable. Any malformed
percent escape preserves the entire original value, including plus signs.

Response crypto now has distinct context lifecycle names and an identity different
from the compiled entry's existing pure context. Tests reject each other's context
before reading the incompatible workspace. Capacity testing also caught an internal
status-code/boolean mixup that could publish an empty encrypted cookie for oversized
JSON; the corrected helper returns a failure with zero output length.

## SQLite concurrency guidance review

Rechecked SQLite's official isolation, transaction, busy-handler, WAL, synchronous
and checkpoint documentation on 2026-10-12. The strict baseline remains private
worker-owned connections, local WAL, FULL durability, foreign keys enabled,
short DEFERRED read snapshots and IMMEDIATE read-modify-write transactions.
Multiple readers may overlap one writer; extra workers do not create additional
simultaneous writers. No SQL mailbox is required.

Updated [the policy](../docs/SQLITE-POLICY.md) and
[delivery plan](../docs/CAMPFIRE-AOT-PLAN.md) with required accommodations for
atomic dependent operations, remaining-request-time lock waits, cancellation
cleanup, diagnostic error codes, checkpoint pressure and bounded resource use.
The existing 252 adapter verifies its strict settings and transaction cleanup;
its fixed five-second busy timeout is not deadline integration, and composition
of independent callbacks is not a shared transaction. Checkpoint monitoring,
write-route atomic scopes and broader concurrent HTTP evidence remain unfinished.

PASSIVE checkpoint completion must be assessed using frame progress, not success
status alone. Blocking checkpoint modes must not run on every request. Process
crash/reopen tests are required before production durability claims and do not
substitute for power-loss testing. These are recorded requirements, not new
runtime capabilities claimed by this documentation update.

Source audit confirms READWRITE/NOMUTEX/PRIVATECACHE opens, patched-version checks,
startup WAL provisioning, effective worker settings, DEFERRED message snapshots,
IMMEDIATE refresh, and rollback/autocommit/poison checks in the adapter. The final
252 gate has 234 assertions including old/fresh snapshot visibility and bounded
writer contention followed by reuse; its separate cleanup probe forces rollback
denial and verifies rejection. This is not yet evidence of many native workers,
checkpoint starvation/recovery or request-deadline cancellation. Documentation
validation for this update: local Markdown targets exist and git diff --check
passes; runtime source and ABI are unchanged.

Sources: [isolation](https://www.sqlite.org/isolation.html),
[transactions](https://www.sqlite.org/lang_transaction.html),
[busy handlers](https://www.sqlite.org/c3ref/busy_handler.html),
[WAL](https://www.sqlite.org/wal.html),
[synchronous](https://www.sqlite.org/pragma.html#pragma_synchronous),
[checkpoint API](https://www.sqlite.org/c3ref/wal_checkpoint_v2.html).

## Initial real HTTP samples — checked runtime

The fresh standalone host serves authenticated room 104 using the pinned 252 O2
entry and SQLite 3.53.3. GET returns 200 with 4,480 bytes and forty message
articles; HEAD returns 200 with no body and Content-Length 4,480. Both set
last_room. Twenty cases match the Kestrel/interpreted control in status, body
SHA256, content type, redirect location and cookie names. The corpus covers
missing/tampered/duplicate/whitespace cookies, room access, routing, methods and
decoded/case-insensitive/duplicate bot-key query names. Encrypted cookie contents
and HTTP framing are not established by this comparison alone.

Five-second sequential closed-loop samples, twenty warmups each, full body hash
verification on every request, four native workers and a 64-request native queue:

| Client concurrency | Native correct req/s | Reference correct req/s | Native p99 upper bound | Reference p99 upper bound |
| --- | ---: | ---: | ---: | ---: |
| 1 | 5.05 | 122.20 | 290 ms | 15.284 ms |
| 4 | 21.12 | 125.57 | 290 ms | 38.98 ms |
| 16 | 20.85 | 125.84 | 870 ms | 160 ms |

All six samples have zero response failures. Sampled native working-set peak is
31.1 MiB; reference grows to 404.4 MiB. Native private committed bytes peak at
49.7 MiB versus 534.9 MiB reference. Sampling is every 100 ms and excludes
HTTP.sys kernel memory. Native scrubs a 65,536-byte request buffer per request;
the current runtime retains ownership/type/cursor checks. These short single
samples are diagnostic, not a sustained saturation result or published Rust/Rails
comparison. Repeats and longer runs remain required for a final benchmark.
The recorded workstation is Windows 11 Pro build 26200, Intel Core Ultra 9 185H
(16 cores/22 logical processors), approximately 63.5 GiB physical RAM, .NET SDK
9.0.308 and clang 19.1.5. CPU affinity and thermal state were not controlled.

Native CPU accumulated 59.27 seconds across this process's measured/warmup work,
supporting CPU overhead rather than a TCP wait as the next investigation. The
runtime repeatedly validates type graphs and clears fixed-capacity memo arrays;
the isolated scalar memo experiment improves that small operation substantially,
but populated-entry counters show only 51 graph validations per request, so it
is not the dominant cost. Correctness checks remain while testing a focused
optimization. The loaded reference
SQLite DLL was inspected and its SHA256 matches the patched native pin.

A private instrumented rebuild of the same populated entry isolates the main
cost: 25,175,528 bytes reserved/released per request, and 50,351,056 bytes handled
by diagnostic poisoning/bitmap marking. Six callbacks reserve the available
4 MiB arena tail; reserve and release bookkeeping process it again. Per-data-byte
bitmap updates account for approximately 49.1 of 58.7 ms/request in that run.
The six SQLite/crypto callbacks together take approximately 0.32 ms/request.
There are 298 VM steps and 410 trace events. These are isolated diagnostic
measurements, not a production throughput result. Two private prototypes retain
all poisoning and bitmap checks, batching disjoint bitmap ranges and caching the
fill destination only when disjoint from the runtime context. Overlapping ranges
retain the original byte-loop sequencing. Their serial entry runs measured
26.96–26.98 ms with bitmap batching and 20.65–20.81 ms with both changes; request,
typed output and trace hashes match. The accepted DLL itself varied from
65.72 ms on its warmed run to much slower earlier runs, so these numbers cannot
establish a server speed ratio. Tracked runtime promotion is underway with
independent review and fresh O0/O2 differential and application gates; HTTP
throughput must be measured again after those pass.

The tracked guarded runtime is frozen at SHA256
`7d2824cb4b8592057ab9a106db818e1c7fb68ed95a99c01c85e698a8abc1b3f5`.
`Verify-OwningStackRuntimeRanges.ps1` records O0/O2 exact differential passes
for 266,260 small bitmap cases, 66,576 fill/error cases, and three 4 MiB cases
for each operation. Distinct, identical and shifted bitmap aliases, context
overlap, invalid ranges, zero length, address overflow and untouched canaries
are covered. Trusted-profile runtime objects also compile under strict warnings.
ABI/layout headers and the deterministic native ABI fixtures remain unchanged.
The fresh room-read gate passed 234 assertions at O0/O2, including typed
interpreter/native parity, standalone no-CLR execution, effective SQLite
settings, snapshots, contention and clean reuse. Its 63 source pins remained
stable in run `20261012T231453845Z-0fa3e7ea7626414bb6890c5355a0dc38`.
The new O2 DLL is
`e4bf24e25227321d1e8805806fdf513bd35bfd190fa278941465771c4e516d87`;
manifest `0eaf8b3ab7d3a6c8ecee3f6062bcf4a50604e4401f95148446f1e321346473ed`;
SQLite remains `b7385d722c83fb52142a00477a726723745916d22a555711ee89834c1111fb2e`.
The receipt now records the SQLite binary alongside module and manifest hashes.
This remains a room-read gate; fresh HTTP corpus/performance and the complete
application migration are separate requirements.

The rebuilt tracked O2 host starts successfully against the exact fresh receipt
and patched SQLite dependency. Its 20-case HTTP corpus passes expected statuses.
Comparing to the earlier control capture initially found one extra session-token
cookie on the first authenticated GET: the live fixture had crossed the
one-hour refresh threshold. The control's next first GET emitted the same cookie.
After both fixtures were refreshed, newly captured corpora agree in all twenty
cases for status, body bytes/hash, content type, location and cookie names.
The initial discrepancy is retained in `optimized-parity-differences.json`;
the new match is `optimized-warm-parity.json`. Cookie content/attribute decoding
and framing coverage remain separate from this metadata comparison.

Private raw results and redacted corpus files are under
`.agentlang/campfire-native-http-253/native-smoke/`. Configurations, database
files and cookie values must be excluded from the eventual evidence archive.

## Pre-expiry-fix checked-runtime HTTP measurements

Two 15-second samples per client concurrency used the final pinned O2 host,
four workers, twenty warmups per sample, complete-body hashing and in-flight
drain. All twelve native/control samples completed with zero status/body failures.
The fixtures' sessions were refreshed before measurement, so this is a steady
authenticated room-read workload, not sustained concurrent application writes.
The full fresh LLVM suite also passes 1,010 assertions; native storage passes
58 cases/872 checks per O0/O2. Builds and regression runs completed before timing.

| Concurrent clients | Native status/body-validated req/s, repeats 1 / 2 | Control status/body-validated req/s, repeats 1 / 2 | Native p99 upper bound ms | Control p99 upper bound ms |
| --- | --- | --- | --- | --- |
| 1 | 46.94 / 47.24 | 199.41 / 152.36 | 24.222 / 22.265 | 8.809 / 23.706 |
| 4 | 180.81 / 174.99 | 188.42 / 154.84 | 29.267 / 39.734 | 36.455 / 56.615 |
| 16 | 178.29 / 180.41 | 183.03 / 145.47 | 110 / 110 | 160 / 190 |

The four-worker native host reaches approximately 175–181 status/body-validated requests/sec
and stays near that rate with sixteen clients. Its sampled process working set
peaks at 32.5 MiB and private bytes at 50.1 MiB. The control's working set grows
across samples from about 350 MiB to 881 MiB, with private bytes reaching about
1,009 MiB. This growth needs attribution before claiming a general memory
advantage; process RSS does not distinguish retained state from managed GC
heap capacity. The control's throughput also varies substantially between repeats.
Source review found no request-count-growing trace/task-log/response collection
in the bound interpreted HTTP path. The control declares Server GC, and its
Transport serializes interpreter calls. Those observations make heap capacity
and transient queued requests plausible contributors, but they do not establish
the cause. Before a memory-efficiency claim, collect managed live-heap/GC counters
and compare under a fixed memory limit; do not label the RSS trend a leak.

The repeated native numbers are much better than the initial 5-second checked
samples, but those earlier runs and the control's changing behavior are not a
controlled release-runtime speed comparison. The compiler-generated release
profile is still required to remove diagnostic bitmap/poison traffic and measure
the intended production behavior. These endpoint results remain far below the
published Campfire targets and use a different workload/platform; they do not
establish equivalent application performance or completion.

Final host SHA256: `130616067520370C775143880333AC37D53107CE9EC89E3AF1E60B27A01F31D8`.
Raw results are in `native-smoke/guarded-four-worker`; their private configurations
are excluded from public evidence. CPU affinity and thermal state remain uncontrolled.

Subsequent cookie/header validation found a real defect in that host: its
permanent-cookie formatter incremented the year by twenty but retained the old
weekday. It emitted `Mon, 12 Oct 2046 23:38:48 GMT` even though that date is Friday.
The original parser rejected it; the diagnostic is retained without cookie values.
These earlier measurements are consequently diagnostic status/body throughput,
not fully correct HTTP performance. The replacement pure Gregorian helper
matches .NET AddYears, recomputes weekdays, clamps leap days and rejects year
overflow. Fresh O0/O2 calendar/header/endpoint tests and corrected-host timing
are required before the milestone is accepted.

## Corrected host acceptance and performance

The corrected formatter passes 9,600 independent .NET oracle comparisons across
the 2000–2399 Gregorian cycle at O0 and O2, plus eleven anchor cases covering
negative Unix microseconds, minimum year, leap-day clamping and the last valid
expiry year. Invalid ranges, null outputs, all capacities below 29 and output
canaries are checked. Source pins are stable in expiry run
`20261012T234204928Z-5c6bda224c284480bd3cbca9ac8764f6`.

Actual O0/O2 HTTP tests each pass six paired cases, twelve requests and 291
assertions: decrypted remembered-return URL/flash state, last_room, cookie
attributes and valid Expires calendar weekdays. Native HEAD sends no body and
has GET-equivalent Content-Length; the existing control omits that framing, a
documented difference. The relocated tracked checker builds with zero warnings
and also passes 291 assertions against O2. Both corrected hosts match all twenty
status/body/metadata corpus cases against the warmed control. Five additional
O2 transport cases reject wrong Host port, oversized selected cookies and total
cookie headers, then demonstrate clean reuse. HTTP.sys rejects the largest
header first with 400; the host's 431 branch is not claimed for that request.

Two fresh 15-second samples at each client concurrency use the corrected O2
binary. The one-worker samples use the same binary/dataset after a clean pool
shutdown. All eighteen samples have zero status/body failures, with headers
independently validated above.

| Clients | One native worker req/s, repeats 1 / 2 | Four native workers req/s, repeats 1 / 2 | Interpreted control req/s, repeats 1 / 2 |
| --- | --- | --- | --- |
| 1 | 47.73 / 47.86 | 47.10 / 47.18 | 157.90 / 139.43 |
| 4 | 48.13 / 48.09 | 177.62 / 178.10 | 160.07 / 138.50 |
| 16 | 48.12 / 48.09 | 177.57 / 177.20 | 155.08 / 133.02 |

At four clients, pool throughput is approximately 3.69–3.70 times single-worker
throughput. At sixteen clients, one-worker p99 upper bounds are 350 ms; four-worker
bounds are 120 ms. At four clients the corresponding bounds are about 90 ms and
32 ms. Extra client concurrency does not materially increase throughput after
the worker pool is occupied. This supports the selected-worker/direct-SQL flow
for this read workload, not write-heavy or slow-client/cancellation behavior.

Sampled native peak working set is 14.8 MiB with one worker and 32.4 MiB with four;
private bytes are about 14.4 MiB and 50.1 MiB. The long-lived control reaches
about 1,197 MiB working set across the corrected repeats. As discussed above,
managed live-heap/GC attribution and fixed-memory comparisons are still needed;
the RSS observations alone do not establish a general memory-efficiency ratio.

Corrected host source SHA256:
`BE32FA33CC42243EA6181FC56CC3A9E6D056614E1B065B6076297789CCC134C6`.
Corrected O2 executable:
`92D59AC703F412BD4B6515EF7DCBD196ADC5560E954F5CDF06A66F175438392A`.
Room module/manifest/SQLite remain the fresh receipt's hashes stated above.
Final build root is `build-tracked-20261012T234217281Z-048c3176dffe413d9a64081cfaa56296`;
raw results are `native-smoke/corrected-four-worker` and `corrected-one-worker`.
All native O0/four-worker/one-worker runs shut down with the exact expected
cleanup counts and zero fatal status. The reference also stops cleanly.

Next: validate the existing compiler-generated release profile on the same typed
boundary and endpoint corpus, then measure without diagnostic bitmap/poison
traffic. Add strict transactional write operations/deadlines as native routes
expand. Full native Campfire still requires the remaining pinned routes,
protocols, assets, jobs, storage and deployment. Published Campfire throughput
remains a target, not a comparable speed ratio from this checkpoint.

## Sealed evidence

[Archive](evidence/253-native-campfire-http/253-native-campfire-http-evidence.zip)
and [manifest](evidence/253-native-campfire-http/manifest.json) retain the final
builds, 103 source snapshots, test receipts, redacted HTTP cases and corrected
one/four-worker raw samples. ZIP SHA256:
`6fa43133bc6acad3bd1880581d69fa662d1f0f0b8def7ba62080c5b5eff3d858`;
4,056,991 bytes, 201 entries including the embedded manifest. Private cookie
configurations, fixture databases and secret files are excluded. The archive's
per-entry hashes and current source pins were independently rechecked before
commit. The report itself stays outside the archive to avoid a digest cycle.
