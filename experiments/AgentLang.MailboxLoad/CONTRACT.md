# Matched mailbox load experiment (140)

This is a bounded request-orchestration benchmark, not an HTTP service or an
agent-efficacy trial. Reuse `AgentLang.RealIoMailbox/mailbox.flow` and its exact
initialize/begin/resume transitions. Do not change the established correctness
fixtures or native runtime to improve benchmark results.

## Implementations

- Native: fresh O2 trusted-generated mailbox DLL and host, with 16 persistent
  mailbox identities, 4 scratch slots of 262144 bytes, 65536 retained bytes per
  bank and 16384 bytes of text staging. Compare KEEP and RETURN with identical
  reservations. One pending operation per identity; reject unavailable identities
  or scratch admission immediately. No hidden request queue.
- F#: Release JIT, ordinary immutable State/Continuation records and strings,
  exact same transitions. One serial state owner and asynchronous sockets;
  completion tasks must not mutate State. Bounded pending operations (16), no
  artificial four-slot admission restriction. The process memory ceiling is
  common; native's arena availability is an actual resource constraint.
- Provider: common bounded echo protocol, an opt-in persistent mode of the
  existing provider. Maintain the existing one-shot mode and output schema.
  Open 16 connections before warmup, reuse one connection per mailbox, allow
  only one outstanding frame per connection. Provider delays and payloads are
  identical across backends. Do not create a socket per request.

The benchmark schedules its own deterministic open-loop arrivals. This avoids
building a new ingress service before the first comparison, but includes arrival
scheduling overhead in each implementation; record that limitation explicitly.
Offer request sequence n to mailbox n mod 16. Arrival times derive from the
monotonic epoch and configured rate, never from prior completion. Busy arrivals
are rejected, not rescheduled. Bound catch-up batches (64) and service I/O between
batches. Latency starts at the scheduled arrival, including dispatch lateness.
Admission records are benchmark-owned, separate from language state.
Schedule n=0 at the epoch; expected offered count is ceil(rate*durationMs/1000).
If the owner reaches the deadline with scheduled arrivals still undispatched,
classify all remaining arrivals as offered/rejected/missed, without admitting
work after the window. Record `missedArrivals` (a subset of rejected) and
`maxDispatchLatenessMicroseconds`; silently omitting scheduled arrivals is invalid.

## CLI contract

Both apps accept the following option/value pairs (unknown/duplicate options
and invalid ranges are errors):

```
--port PORT --rate RATE --payload-bytes BYTES --delay-ms DELAY
--warmup-ms MS --duration-ms MS --output ABSOLUTE_JSON_PATH
```

Native additionally requires `--module DLL --policy return|keep` and accepts
`--scratch-slots 4|16` (default 4). Report `scratchSlots` in native results.
Limits: port 1..65535, rate 1..64000, payload 1..4096, delay 0..1000,
warmup 0..10000 ms, duration 100..30000 ms. Use a finite 5-second drain deadline.
All runs exit nonzero on protocol, state, I/O, timeout or invariant errors, while
ordinary admission rejection is reported and is not an executable failure.

Use the same deterministic ASCII payload for request and provider echo: byte i
is `a + (i mod 26)`; request sequence does not need to be embedded in text. Thus
successful latest State equals payload concatenated with itself. Verify provider
echo bytes, final per-mailbox attempted/completed counters against independent
admission/completion counters, and final latest content. Do not only assert that
the application reports success. The existing correctness suite covers varied
content, cancellation and failed-resume isolation separately.

Warmup uses the same workload, drains fully, then reinitializes application
state/counters for measurement while keeping connections and warmed code.
Native must dispose and reinitialize the runtime in its existing caller storage
after full warmup drain, then initialize all identities (already initialized
mailboxes cannot simply be initialized again). F# resets its records/counters.
Score arrivals scheduled before the measurement deadline. Count completions
before deadline separately from completions during drain. Report all measured
request latencies including drain; warmup must not enter measured counters.
No cancellation in scored trials. Run bounded shutdown/drain and existing
cancellation/slow-response correctness checks before interpreting throughput.

Result JSON schema (camelCase, schemaVersion 1):

```
backend: "native" | "fsharp"
policy: "return" | "keep" | "gc"
rate, payloadBytes, delayMs, warmupMs, durationMs
warmupCompleted, offered, admitted, rejected, completed, completedWithinWindow
errors, timedOut, pendingAtEnd, peakPending, verified
missedArrivals, maxDispatchLatenessMicroseconds
latencyMicroseconds: [one integer per measured completion, including drain]
mailboxes: [{id, attempted, completed, latestVerified} x16]
storageReservedBytes: native caller storage, null for F# (not process memory)
```

Use offered=admitted+rejected and admitted=completed+errors+timedOut after drain;
for successful correctness runs errors=timedOut=pendingAtEnd=0. Counters are
integers; raw samples permit an independent nearest-rank percentile calculation.
Native may add native runtime/copy stats. F# may add GC counts/allocation bytes.
Keep output and timing instrumentation outside the handler implementation.

## Measurement contract

Use a small Windows Job Object launcher, create children suspended, apply an
application process-commit ceiling before resume, and kill the job on timeout.
Record actual peak committed bytes (Job Object) and peak working set (process),
CPU time, exit code, enforced limit and executable/source/build identity. Do not
describe committed memory as resident memory. The common provider is measured
separately and explicitly excluded from each application's ceiling. No monitor
based on occasional RSS samples may be called an enforced memory ceiling.

Predeclared application ceilings: 256 MiB for all implementations; 320 MiB (+25%)
for the native follow-up point. These may be nonbinding; report that rather than
claiming the ceiling caused a throughput difference. Keep CPU resources equal
and record affinity/runtime/GC settings. Pin all applications to the same available
logical CPU and the provider to a different available logical CPU; record any
single-CPU fallback. Separate affinity does not prove separate physical cores
or eliminate provider bottlenecks. Do not force F# to synchronously poll
or remove its usual safety behavior to imitate native implementation details.

First scored grid: payload 4096 bytes, provider delays 0 and 5 ms, offered rates
250, 500, 1000, 2000, 4000, 8000/s; 1000 ms warmup and 3000 ms measurement,
three repeats with balanced alternating backend order. If the highest rate still
qualifies, extend by doubling up to 64000/s; otherwise report the highest
qualifying *tested* rate, not an exact maximum. p99 target is 50 ms, rejection
fraction <=1%, zero correctness/protocol errors and bounded drain. Retain every
trial, including nonqualifying ones. Run the +25% native point at each policy's
highest qualifying equal-limit rate (or the lowest rate if none qualifies).
Do not interpret a shared-provider bottleneck as a language throughput result.

Predeclared capacity follow-up: four slots deliberately exposes pinned-slot
admission pressure, and cannot establish that KEEP loses with an adequately
sized pool. For each delay, run both native policies with 16 slots at the union
of each four-slot policy's highest qualifying rate and the next grid rate.
If a policy has no qualifying rate, use the lowest two rates. Keep three repeats,
equal 256 MiB ceiling, and identical reservations within each policy pair. Reuse
unchanged F# results at those rates. Extend by doubling only when the highest
sampled rate still qualifies (up to 64000/s); otherwise report tested points.
This adds roughly 3 MiB of native scratch reservation and is a separate capacity
axis, not a replacement of the primary four-slot ranking.

Score successful throughput as completedWithinWindow/(durationMs/1000), and p99
over every measured completion including drain. A grid rate qualifies only when
all three repetitions meet the gates. Confirm each highest qualifying point
with a 30000 ms measured run; if it fails, retain the failure and label the short
grid exploratory rather than calling it sustained throughput. Persistent provider
results must distinguish accepted connections from accepted/completed frames.
Restart the provider per trial and reconcile frame counts with warmup plus scored
application I/O; preserve default one-shot counter semantics.

Run small smoke/overload cases before the scored grid. Stop and report a concrete
invalid-comparison condition instead of manufacturing a winning result. Save
exact build/run commands, configurations, samples, failures and source hashes.
