# 256 — Native room execution breakdown and profiler overhead

Status: detailed profiling and overhead measurement passed; full migration incomplete.

This milestone splits report 255's `api_execute` interval into the nine Campfire
host operations, four SQLite call kinds and nine generated-runtime categories. It
then measures the detailed counters against an otherwise identical build on the
same four distinct physical cores with sixteen clients. Semantic IR, owning-context
and layout ABIs, effects, SQLite settings and the release path are unchanged.

## Implementation and validation

`OwningRuntimeInstrumentation.Off | Detailed` selects a compile-time profile in
the native owning runtime (`owning_stack_profile.h`, ABI v1). Off compiles every
profiler export, import and call out; existing compiler APIs default to Off.
Detailed keeps thread-local bounded counters (calls, QueryPerformanceCounter
ticks and failures) for 22 categories and exports begin/end/reset/snapshot
functions. The room adapter wraps all nine host operations and the SQL
prepare/bind/step/reset calls without changing their results. The HTTP host
resets counters after worker setup and snapshots on each worker before teardown.
Manifests and build receipts pin the selected execution profile and header hash.

Fresh gates passed before measurement:

- Room-read provider gates, O0 and O2 including standalone no-CLR execution:
  Detailed 296 assertions and Off 238, both with stable source freezes.
- Full LLVM suite: 1,010 assertions, zero build warnings, on the exact sealed
  source (rerun for this report after the profiler changes).
- Profiler API probe (26 assertions): failure counter, invalid-argument
  immutability, timer-failure sentinel, no-write snapshot failure, reset recovery,
  and export absence in Off. No real OS timer failure was induced.
- Strict native matrix: Diagnostic/TrustedGenerated × Off/Detailed × O0/O2
  compile, link and import audits. Only Detailed modules import QPC and export
  the profiler; the Detailed O2 module calls it from 48 begin sites.
- Live HTTP for Off-O0, Off-O2, Detailed-O0 and Detailed-O2: 291 cookie/header
  assertions and 20 corpus cases each, plus 5 transport/reuse cases at O2.

## Method

Both arms are O2 `trusted-generated` hosts with the report-255 stage profiler on,
so this measures the *incremental* detailed-counter cost, not stage timing. Each
run is a new host lifetime with four workers, a refreshed synthetic session, two
15-second closed-loop repeats with 20 warmups, 16 HTTP/1.1 keepalive clients and
SHA256 verification of the complete 4,480-byte body on every response. The server
is pinned to mask `0x5401` (four distinct physical cores), and the client to
`0x3f03fc`, which excludes those cores and their SMT siblings. Latency ends when
the full body arrives, before status/digest validation; throughput includes
validation. This boundary differs from report 255, so latencies are not pooled.

Two ABBA sets ran: `Off, Detailed, Detailed, Off` at 22:11–22:16 EDT, then a
replication two hours later. Before collecting set 2, a contamination screen was
written down, derived from set 1's saturated runs. A run is flagged if mean
server CPU is below 3.75 cores, `send_api` exceeds 60 µs/GET, or its two repeats
differ by more than 5%. Flagged runs stay in every table. Set 2 also recorded
process snapshots: only the task's own host, client and shell started during it.
The four set-2 lifetimes used the byte-identical helpers under PowerShell 7.6.6;
the first set's PowerShell version is not recorded.

## Overhead result

| Set | Run | Profile | Correct req/s | `api_execute` µs/GET | Server cores | p99 upper bounds | Screen |
| --- | --- | --- | ---: | ---: | ---: | --- | --- |
| 1 | A | Off | 6,536 | 542.9 | 3.89 | 3.563 / 3.517 ms | clean |
| 1 | B | Detailed | 6,343 | 560.4 | 3.89 | 3.775 / 3.781 ms | clean |
| 1 | C | Detailed | 5,591 | 594.9 | 3.47 | 4.644 / 7.503 ms | flagged |
| 1 | D | Off | 6,308 | 557.8 | 3.81 | 3.619 / 3.624 ms | clean |
| 2 | E | Off | 6,576 | 538.3 | 3.90 | 3.538 / 3.555 ms | clean |
| 2 | F | Detailed | 6,790 | 520.1 | 3.89 | 3.524 / 3.492 ms | clean |
| 2 | G | Detailed | 6,835 | 516.4 | 3.89 | 3.502 / 3.519 ms | clean |
| 2 | H | Off | 6,869 | 513.2 | 3.88 | 3.348 / 3.327 ms | clean |

All 1,555,627 timed responses (excluding warmups) passed; there were zero failures. Fixture session
timestamps were unchanged after every lifetime, and all hosts exited 0 with four
cleaned workers.

**No instrumentation overhead is detectable above the run-to-run drift.** Within
each set, the two Off runs differ by 3.5% (set 1) and 4.5% (set 2) in throughput,
and by 2.7% and 4.7% in `api_execute`. Set 2, the clean replication, shows Detailed
at +1.3% throughput and −1.4% `api_execute` against Off on ABBA averages. In set 1
the clean Detailed run B lies within the Off range for `api_execute`.

Set 1's raw ABBA comparison (−7.1% throughput) is driven by DetailedC. That run
lost server saturation (3.47 cores), doubled its send time and varied 6% between
repeats. Several system processes started during its window; that is observed
context, not a proven cause. Report it as environment-contaminated rather than as
counter cost.

The null result is surprising. Detailed times about 4,170 intervals per GET
(~8,300 QPC reads). A tight-loop microbenchmark on a pinned server core measures
13.0–13.5 ns per QPC call, roughly 110 µs per GET if the costs simply added.
Throughput, server CPU and `api_execute` do not show that cost. One hypothesis is
that the non-serializing timestamp reads overlap with the memory-bound validation
work around them, but this was not tested. Treat the bound as "below the 3–5%
drift floor on this workload and machine", not as a general guarantee.

## Where `api_execute` goes (Detailed builds)

The table gives per-GET means for the clean Detailed runs (set 2 F/G; set 1 B is
within about 10%). They come from instrumented builds and include profiler cost.

| View | Calls/GET | µs/GET | Note |
| --- | ---: | ---: | --- |
| `api_execute` (inclusive) | 1 | 518 | Stage interval |
| Nine host operations (sum) | 6 | 253 | Disjoint; 49% of `api_execute` |
| ↳ `message_last_page` | 1 | 210 | Measure + write passes, 40 messages |
| ↳ `session_find_by_token` | 1 | 17.5 | |
| ↳ `room_find_for_user` | 1 | 10.0 | |
| ↳ `cookie_verify_session_token` | 1 | 6.4 | Pure HMAC/CNG check |
| ↳ `user_find_by_id` | 1 | 5.5 | |
| ↳ `membership_exists` | 1 | 4.3 | |
| SQL step (nested in operations) | 89 | 92 | Not additive with the rows above |
| SQL reset / bind / prepare | 81 / 9 / 0 | 9.0 / 2.3 / 0 | Prepared statements reused |
| Flow/runtime/dispatch outside host callbacks | — | 265 | `api_execute` minus operation sum |

`session_refresh_before`, `user_authenticate_bot_key` and
`browser_version_at_least` were not called by this authenticated GET.

The runtime categories overlap one another and also run inside host callbacks,
so they are not additive:

| Runtime category | Calls/GET | µs/GET | What it wraps |
| --- | ---: | ---: | --- |
| `structural_locate` | 44 | 171 | `locate_field`/`locate_sum_case`: header, type-graph and parent-extent re-validation per access |
| `other_runtime_validation` | 51 | 105 | Type-graph validation of a static layout |
| `initialization_check` | 3,779 | 57 | Mean is below one 100 ns tick; total mostly reflects timer cost |
| `external_measure` | 7 | 22 | Measuring host-provided values |
| `host_output_commit` | 6 | 19 | Typed validation of provider outputs |
| `reserve_release`, `frame_validation`, `arena_copy` | 45 / 33 / 24 | ≤2 each | Sub-tick means; report call counts only |

QPC ticks are 100 ns here. Per-call means far below one tick are averages of
mostly-zero intervals, so they carry no cost information beyond call counts.

## Interpretation

- SQLite execution is not the dominant cost: about 104 µs of SQL calls per GET
  within ~518 µs. Even if every SQL call belonged to the message page, at
  least ~106 of its 210 µs would be outside SQLite, in the adapter's
  measuring/writing passes and output validation.
- The largest attributable runtime cost is re-validating statically known layouts
  on each structural access. That means 44 field/case locates plus 51 type-graph
  validations per GET, and 3,779 initialization checks. The compiler already emits
  these layouts, so the next optimization step is to prove them at build or
  commit time. Runtime checks for unproven paths stay in place. Nothing was
  weakened to obtain these numbers.
- Step time is not lock-wait time. The cookie context's per-worker SRW lock and
  the adapter's atomic re-entry guard are uncontended in this host and are not
  timed separately. HTTP.sys, SQLite and CNG internal synchronization is not
  measured. No lock cause is inferred, and no new lock or cross-worker
  dependency was added.
- Process memory is unchanged by the profile: about 11.7 MiB sampled working
  set and 46 MiB private bytes for four workers in all eight lifetimes.
- This private Windows HTTP.sys workload (40 messages, minimal 4,480-byte page)
  is not the public Rails/Rust room-page benchmark. Do not convert it into a
  speed ratio against published results.

## Retained failures and deviations

- `off-a`: a host lifetime that served zero requests while the measurement
  helper was being corrected. It is retained in the evidence; `off-a-final`
  replaced it.
- The aggregator was revised after set-1 collection. Its original version
  required whole-document equality of the pins header and source freeze, and
  those necessarily differ by profile. The revision checks profile-neutral
  source maps instead. Manual diffs confirm that only the profile flag and the
  module/manifest hashes differ. The sealed `aggregator-revision.md` gives
  details, and run-start receipts pin the pre-revision hash.
- The full LLVM suite was rerun for this report: an earlier sealed-path draft
  referenced a pre-profiler run.

## Sealed evidence

[Archive](evidence/256-native-campfire-profiling/256-native-campfire-profile-evidence.zip)
and [manifest](evidence/256-native-campfire-profiling/manifest.json) retain 369
entries. They include:

- all eight accepted runs plus the failed `off-a`, with raw per-worker stage and
  detailed counters, client samples, affinity, fixture and terminal receipts;
- both aggregations, the pre-registered screen and its results, set-2 driver logs
  and process snapshots;
- the QPC microbenchmark and the aggregator-revision note;
- the Off/Detailed O2 host, module, manifest and SQLite binaries with build
  receipts, room-read gate and HTTP validation receipts;
- the profiler API probe, the full LLVM suite receipt and binaries, and 101
  current source snapshots.

ZIP size is 7,669,629 bytes; SHA256:
`afda9d982b4eedde7ebafaf84e91808e95b43f5d7b8d8b3fcc0f2be7b22c9994`.
`verify_archive.py` checks every entry against the manifest. Fixture databases,
WAL/SHM files, secrets, cookie values, request/measurement configs and raw HTTP
headers/bodies are excluded, and text entries were scanned for cookie values.
All task-owned listeners stopped with exit 0 and four cleaned workers. CI remains
manual-only.

## Next steps

1. Use the matched room-page comparison to close the workload gap against the
   pinned shared harness. Label the platform difference (Windows HTTP.sys versus
   the published Linux/Docker runs) rather than claiming parity.
2. Prototype compiler-proven structural access for statically validated layouts,
   then re-measure with this ABBA method. Off remains the release default.
3. Native message writes: the membership-and-create IMMEDIATE transaction with
   capacity preflight before mutation. A queued singleton writer remains a
   research candidate to discuss, not an adopted design.
