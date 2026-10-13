# 255 — Native room execution profiling

Status: profiling and fixed-four-core comparison passed; full migration incomplete.

This milestone measures the reproducible worker-concurrency regression from
[report 254](254-native-campfire-release-profile.md). It adds an optional
compile-time stage profiler, preserves the normal release path, and records
native server and load-generator process CPU separately. It does not add a
database worker or change transaction/durability behavior.

## Implementation and validation

`Build-CampfireNativeHttp.ps1 -StageProfile On` selects numeric worker-local
QueryPerformanceCounter totals, maxima and calls for receive wait, input
preparation, native language/SQL execution, both renderer passes, cookie
finalization, sending and sensitive/request-buffer cleanup. Per-worker CPU is
captured after allocation and before release. Totals are emitted after shutdown;
no request contents, authentication values or private input paths are logged.

Profiling is off by default and pinned in the generated header/build receipt.
Off O0/O2 executable import audits require no QueryPerformanceCounter,
QueryPerformanceFrequency or GetThreadTimes imports; On requires all three.
Type/refinement/capacity checks, runtime ABI, closed language-module effects and
SQLite settings are unchanged. The existing validated release module is reused
with its receipt/manifest/SQLite hashes pinned to report 254's artifacts.

Both Off/On host builds passed strict O0/O2 compilation and import audits.
Each live host combination passed 291 cookie/header assertions and twenty HTTP
cases. Five O2 transport/reuse cases passed for each setting. Fresh client builds
passed their histogram boundary/quantile self-test and had zero build warnings.
The first CPU-only client and subsequent optional-affinity client source snapshots
match checksums retained in their compiler Portable PDBs; they are distinct builds.

## Interpretation boundaries

`api_execute` includes generated semantic IR, native owning runtime checks,
SQLite provider callbacks and incoming token verification. It cannot distinguish
those components individually. Renderer totals include two passes; average per
pass is not average per request. Receive wait includes startup, inter-run and
shutdown idle time and must not be counted as request service latency. QPC maxima
are single worst observations, not p99. Client-reported p99 covers full verified
responses; stage timings cover successful scopes and omit some route/header
policy and body-commit work. Worker-loop CPU includes warmups and idle gaps;
client process-CPU intervals closely bracket each timed sample.

The same authenticated forty-message, 4,480-byte room body is checked by complete
SHA256 on every request. Measurements are closed-loop HTTP/1.1 keepalive with
two 15-second repeats and twenty warmups per repeat. Compilers/tests and the
interpreted reference are stopped before timing. No thermal/frequency control
is applied; the laptop's heterogeneous cores and scheduling are possible factors.

The pinned public reference reports four hardware cores and sixteen clients on
an AMD Ryzen AI MAX+395/32GB system. A local affinity comparison constrains our
native process to four logical processors from four distinct physical cores,
with load generation outside those cores and their SMT siblings. This is a local
fixed-budget test, not equal hardware or a whole-machine/kernel CPU quota.

## Results

All twelve 15-second samples passed status/body checks, with zero failures.
The four-core server mask is `0x5401`: logical processors 0, 10, 12, 14 mapped to
physical cores 0, 9, 10, 11, all Windows efficiency class 1. The client mask is
`0x3f03fc`, excluding those cores and all their SMT siblings. Actual masks are
checked before/after each fixed run and inside the client before warmup.

| CPU placement | Stage profiling | Workers / clients | Combined correct req/sec | Server CPU equivalent cores | p99 upper bounds, repeats |
| --- | --- | --- | --- | --- | --- |
| Unbound | Off | 4 / 8 | 6,405 | 3.922 | 2.102 / 2.029 ms |
| Unbound | On | 4 / 8 | 6,399 | 3.920 | 2.052 / 2.009 ms |
| Unbound | On | 8 / 8 | 3,334 | 7.625 | 4.535 / 4.922 ms |
| Unbound | On | 8 / 4 | 6,340 | 3.676 | 1.121 / 1.099 ms |
| Four distinct cores | On | 4 / 16 | 6,536 | 3.883 | 3.460 / 3.456 ms |
| Same four cores | On | 8 / 16 | 6,363 | 3.922 | 10.633 / 10.310 ms |

The paired unbound Off/On four-worker averages differ by less than 0.1%, within
the observed repeat variation. That supports low overhead for these samples;
it is not a universal overhead guarantee. Measured client CPU averages remain
under 0.6 cores, making load-generator CPU saturation less likely in this setup.

For unbound four-worker execution, mean `api_execute` wall time is 552 µs/GET,
about 89% of the profiled service total. At eight active workers it is 2,258 µs/GET,
while server CPU nearly doubles and throughput nearly halves. Input preparation
remains 3–4 µs, both renderer passes together 18–20 µs, cookie finalization under 1 µs,
send 40–47 µs and cleanup 4–5 µs. The concentration is inside the generated
language/runtime/provider call, not the request decoder or arena scrub.

Constraining CPU placement and separating the client restores eight-worker
throughput to 6,363/sec. The fixed eight-worker profile has longer wall intervals
(including preemption/waits): native execution 848 µs and sending 312 µs per GET,
versus 542 µs and 41 µs with four workers. Its service mean is 1,229 µs versus 607 µs;
these are aggregate worker wall intervals per request, not end-to-end latency.
The worse eight-worker tail is consistent with oversubscription/scheduling costs,
but this experiment does not identify a specific scheduler rule or lock.

The fixed-budget comparison changes placement, client build and concurrency
relative to the original matrix; it is not a single-factor proof of the earlier
regression's cause. It does show that eight allocated request workers are not
intrinsically limited to 3,300/sec. Keep four workers as the default for this
four-core budget and use explicit CPU budgets for future comparisons.

Native sampled resident sets stay roughly 13–15 MiB while private bytes remain
about 46 MiB for four workers and 89 MiB for eight. Kernel buffers are excluded.
Scratch/data arenas, release semantics and capacity limits have not changed.
The fixture session timestamps are identical before/after profiling, remain
under one hour old, and are inspected using the same pinned SQLite 3.53.3 library;
no session-refresh write is observed across this profiling matrix.

The remaining performance investigation is within `api_execute`: distinguish
SQLite query/allocator/lock costs from typed layout/arena validation and generated
IR work. These measurements neither prove a singleton SQL worker necessary nor
rule out SQLite contention. Do not weaken type, capacity, isolation or durability
checks to obtain a faster number. Native write routes remain the next application
step, with focused profiling continued where it informs their implementation.

## Architecture follow-up

The user proposed a singleton asynchronous database worker. Compare a bounded
writer queue when native writes exist; retain concurrent reads initially.
Transactions should represent logical operations. Cross-request batching needs
explicit failure, commit acknowledgement, delay, ownership and cancellation
semantics. SQLite's single-writer limit remains; queueing does not create parallel
writes. This remains a candidate, not an inference that SQL caused this regression.
See [the strict SQLite policy](../docs/SQLITE-POLICY.md).

The next native application step is the existing message-create flow. Its managed
provider atomically writes message/rich text/timestamps, but membership checking
is outside that transaction. A typed membership-and-create operation must close
that gap, preflight output capacity before mutations and preserve rollback/error
behavior. Attachments, post-commit live delivery/webhooks/push and the documented
Rails CSRF divergence remain explicit full-migration requirements. A synchronous
transaction slice must not mark the complete POST route or migration finished.

## Sealed evidence

[Archive](evidence/255-native-campfire-profiling/255-native-campfire-profile-evidence.zip)
and [manifest](evidence/255-native-campfire-profiling/manifest.json) retain 113 entries
including raw per-worker timings, all 12 client samples, exact source/client/build
pins, topology and affinity receipts, HTTP checks, session-age observations and
the source-based native message-write plan. ZIP size is 1,771,106 bytes; SHA256:
`81b049cf37e4f52b07581e3bd7f126b4cf6248be50299e9a4e8ccc6aa603a757`.
The unchanged compiler/module baseline is linked by the sealed report 254 archive
SHA256. The profiler and client changes have their own source snapshots.
Private authentication configurations, cookie/secret values and fixture databases
are excluded. All task-owned listeners stopped with terminal exit 0 and complete
native worker cleanup/fatal status 0. CI remains manual-only.
