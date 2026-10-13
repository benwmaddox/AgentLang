# 254 — Native Campfire release-runtime profile

Status: validated native room-read checkpoint; full migration incomplete.

The [checked native HTTP checkpoint](253-native-campfire-http.md) reaches about
48 room reads/sec with one worker and 177–178 with four. Diagnostic poisoning
and bitmap updates still process roughly 50 MB per request. This milestone
connects the existing compiler-generated release profile to the same typed
native entry and HTTP host, then measures the same workload.

The semantic IR, effect allowlist, serialized schemas and ABI remain unchanged.
Type/refinement, layout/extent, capacity and host-output validation must remain
active. The release profile omits diagnostic initialization/poison tracking,
trace events and related diagnostic scans; it trusts verified compiler-generated
arena writes. It does not make arbitrary native code safe or dynamically detect
uninitialized reads.

The compiler API selects the owning runtime profile separately from the closed
Campfire host capability profile. The selected runtime profile belongs in the
manifest fingerprint. The HTTP executable receives a fixed profile pin from a
passing artifact receipt; its context has either diagnostic bitmaps or exactly
null bitmap pointers and zero bitmap capacity. A runtime CLI switch cannot
change that pairing.

Acceptance requires fresh O0/O2 application parity, actual-export malformed
input and capacity rejection, and cross-profile context rejection. The existing
HTTP corpus and decrypted cookie/header checks run before authenticated timing.
Reports must distinguish omitted diagnostics from semantic checks still tested.

## Validation completed

Both selected-profile room gates passed 238 assertions, stable 63-source freezes,
O0/O2 typed-result parity and standalone no-CLR execution. The raw exported-entry
probes reject the opposite profile's bitmap shape, missing input/extents/provider,
wrong argument count, truncated extent, invalid UtcInstant refinement and
insufficient retained capacity without partial output. The existing read-snapshot,
contention and connection-reuse cases also passed in each profile.

The fresh full LLVM suite passed 1,010 assertions with a fresh isolated build
(zero warnings/errors). This covers compatibility of existing diagnostic callers,
native module metadata and the existing two-profile module path. Runtime C/header
bytes and layout5/runtime2/host-callback1 ABI versions remain unchanged.

Both profiles' HTTP hosts built at O0/O2 with strict compiler flags and import
audits. The host optimizations use the gate's pinned O2 language module; the
separate room gates test both language-module optimizations. There is no CLR or
static SQLite import in the host: it loads the pinned SQLite3.53.3 C library.
Unknown profiles and selecting either profile with the other's gate receipt are
rejected. The generated pin header, runtime compile define, build receipt and
worker context agree on profile.

All four live host combinations passed 291 cookie/header assertions each and
twenty HTTP cases matching the warmed local control's status, body hash, content
type, redirect and cookie names. Five O2 transport-bound/reuse cases passed for
each profile. HEAD framing remains deliberately recorded: native advertises the
GET body length; the current interpreted control differs.

One first diagnostic-O2 cookie check failed on missing-room cookie names. The
unchanged rerun passed after both servers processed that request. Reusing the
synthetic sessions across the one-hour refresh boundary plausibly caused an
additional session_token cookie, but the failed checker did not retain its
observations, so this is not a proven diagnosis. The transient failure is retained
in evidence; a refresh-aware failure receipt remains a harness improvement.

## Limits and interpretation

Release mode trusts verified compiler-generated arena writes; it no longer
dynamically detects reads of uninitialized bytes. Host-provided native code and
raw ABI storage/lifetime declarations remain trusted contracts. The independent
source review found type/refinement, extent/capacity, effect and host-result checks
still active. The managed test executor retains an unused trace-buffer allocation;
the standalone HTTP worker uses zero trace fields.

The host retains the existing 4 MiB whole typed-response ceiling, beneath the
16 MiB adapter/render limits. Capacity-minus-one rejection is tested; an exact
4 MiB/4 MiB+1 large-response endpoint fixture is still missing. No limit was
increased in this milestone.

## Authenticated release-profile performance

Each row retains two 15-second samples. Closed-loop HTTP/1.1 keepalive clients
verify status and the full 4,480-byte body SHA256 on every populated 40-message
room response. Twenty warmup requests precede each sample; in-flight drain is
included. All 28 samples (22 native, 6 control) have zero status/body failures.
Builds and tests were finished before timing. No affinity or thermal control was
used on the same Windows11/Core Ultra9 185H/22-logical-processor machine as253.

| Native workers | Concurrent requests | Correct responses/sec, repeats | p99 upper bounds, ms |
| --- | --- | --- | --- |
| 1 | 1 | 2,777 / 2,766 | 0.592 / 0.565 |
| 1 | 4 | 3,162 / 3,146 | 1.674 / 1.999 |
| 1 | 16 | 3,167 / 3,122 | 6.031 / 7.599 |
| 4 | 1 | 2,608 / 2,764 | 0.644 / 0.489 |
| 4 | 4 | 5,938 / 6,438 | 1.247 / 1.093 |
| 4 | 16 | 5,917 / 6,045 | 3.877 / 3.840 |
| 8 | 1 | 2,706 / 2,774 | 0.546 / 0.477 |
| 8 | 8 | 3,235 / 3,295 | 4.548 / 4.328 |
| 8 | 32 | 3,202 / 3,244 | 12.560 / 12.471 |
| 8 | 4, crossover | 6,112 / 6,040 | 1.168 / 1.196 |
| 4 | 8, crossover | 6,434 / 6,468 | 2.022 / 1.978 |

The crossover separates allocated workers from active execution: eight workers
with four concurrent requests retain approximately 6,000/sec, while eight concurrent
executions fall to 3,200–3,300/sec. Four workers with eight concurrent requests queue
the excess and retain 6,400/sec. This supports a contention hypothesis once too much
work runs simultaneously; it does not identify SQLite, HTTP.sys, CPU scheduling,
crypto or rendering as the cause. Client-side limits remain possible without
separate load-generator and server-stage profiling. Four workers remain the
default; do not increase the pool as an assumed throughput improvement.

The checked profile's earlier same-endpoint samples were about 48/sec with one
worker and 177–178 with four. The release-path gain is substantial after removing
roughly 50 MB/request of diagnostic poison/bitmap work. It is a comparison of
runtime configurations, not a general language speedup or proof of optimal code.

Peak sampled native working sets were about 9.9/12.8/14.0 MiB for one/four/eight
workers; private bytes about 13.5/46.2/89.4 MiB. These process samples exclude
kernel HTTP buffers and do not establish equal-memory throughput. Fixed worker
buffer allocation explains part of capacity growth; committed/reserved/private
bytes and resident working set are different measures.

The paired .NET/Kestrel + interpreted-language control delivered approximately
159–183/sec at concurrency 1, 164–175 at 4 and 163–177 at 16. Its policy execution is
serialized and SQL connection behavior differs. Managed working set grew from
about 436 to 724 MiB across samples; the earlier attribution/GC caveat still applies.
This is an endpoint reference and harness control, not compiled F#/Rust/Rails.

The observed 6,000/sec is in the same order of magnitude as the recorded published
Rails 4,101/sec target, but the workload, hardware, platform and application scope
are different. No competitive speed ratio follows. Native writes, live delivery
and complete application migration remain unmeasured.

## Next work

Profile the reproducible four-to-eight-active-worker regression with low-overhead
stage/wait/CPU observations and the crossover workload. Keep optional diagnostic
instrumentation separate from the release default. Improve the cookie checker
to retain redacted observations on failure and explicitly test session-refresh
behavior, and add the exact retained-capacity boundary fixture. Continue native
transaction/write/auth routes with atomic dependent operations, bounded busy
handling and cancellation, followed by the remaining full-migration work.

## Sealed evidence

[Archive](evidence/254-native-campfire-release/254-native-campfire-release-evidence.zip)
and [manifest](evidence/254-native-campfire-release/manifest.json) retain both
profile gates/builds, 104 current source snapshots, redacted HTTP receipts,
transient validation history, independent source review and all 28 raw benchmark
samples. The ZIP is 12,248,799 bytes with 273 entries; SHA256:
`5cc2c1b0071ad02f8d47a767b8b385f03b923ea7a976807cec3052cb404a29a7`.
Every archive entry and source pin was independently rehashed after sealing.
Private fixture cookies/configurations, secrets and databases are excluded.
All task-owned native/reference listeners stopped with terminal exit0; native
workers reported complete cleanup and fatal status0.

The complete Campfire migration remains unfinished: this is release-profile
validation for the room endpoint, followed by native transaction/write routes,
remaining protocols/assets/jobs/storage and deployment. Published Campfire
numbers remain contextual targets until equivalent workload evidence exists.
