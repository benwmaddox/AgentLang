# Async arena throughput experiment

Status: approved later research, 2026-10-07. No native allocator or async mailbox
implementation is claimed. This follows agent-behavior validation and the native
memory safety prerequisites in [the roadmap](ROADMAP.md).

## Candidates

Use **per-turn scratch arenas as the main candidate**. A handler turn runs to
completion. Before suspension it transfers only needed continuation data into
explicit bounded retained storage; completion runs in a fresh scratch arena.
Pending I/O, queued messages, connection state and outgoing buffers have defined
owners and limits. Nothing surviving a turn may reference released scratch.

Compare against **whole-request arenas** retained across suspension until safe
completion. Both candidates implement the same workload and observable behavior
on the same backend. Keep the scheduling model fixed in the first comparison;
whole-process versus per-mailbox single-thread execution is a separate variable.
Arena chunk allocation/pooling and safe buffer transfer policies must be recorded,
so changing those does not masquerade as a lifetime-policy improvement.

The working hypothesis is that releasing scratch before I/O waits permits more
useful concurrent requests under a memory ceiling. Copying retained state and
additional transitions may instead limit CPU throughput. Measure both outcomes.

## Safety prerequisites

- Reject escapes into released regions, including nested containers and aliases.
- Retain buffers until providers have actually relinquished access. Cancellation
  requests alone do not authorize reuse or release.
- Bound pending-operation counts, continuation bytes, queued payloads and outgoing
  bytes. Apply backpressure or structured rejection at capacity; record it.
- Define cleanup for success, errors, timeouts, disconnects and cancellation,
  including late completions and prevention of double publication or cleanup.
- Verify equivalent results and cleanup before performance measurements. Include
  failure paths; bulk reclamation does not roll back external effects.

## Controlled load matrix

Run matched workloads with fast I/O, delayed I/O, slow clients consuming output,
timeouts/disconnects/cancellation, and frequent suspensions with small versus
large continuation state. Include CPU-heavy work as a control. Long-lived
connections and large in-flight buffers must be represented rather than excluded.

Increase offered load through saturation and sustained overload. Fix machine,
CPU allowance, process memory limit, payload distributions, provider capacity,
queue limits and workload seed for each matched pair. Use the same build profile
and semantic checks. Record any differences required by the policies explicitly.

Predeclare a workload-specific acceptable tail-latency threshold, error budget,
memory ceiling, warmup, measurement duration and repeat count before collecting
comparative results. Use an external load generator that records offered load
and end-to-end latency from scheduled arrival, including queueing; do not hide
overload by delaying the next request until a previous one finishes.

## Measurements and decision

Primary measurement: **maximum sustained successful completions per second at
the same enforced memory limit and acceptable tail latency**. Failed, rejected
and timed-out requests do not count as successful completions. A run outside
its latency or error budget is not a qualifying throughput result.

Also record p50/p95/p99 latency, errors/timeouts/rejections, offered and admitted
load, in-flight operations, queue depth/bytes, CPU use, continuation-copy bytes
and time, arena used/reserved/peak bytes, and whole-process peak/resident memory.
Account for provider allocations and kernel/socket buffers where measurable;
identify exclusions. An arena counter alone does not measure server memory.

Check that memory and queues reach a sustainable bound, and that cancellation
storms and slow clients eventually release resources without invalid accesses.
Publish repeated results and variability, source/build identity, configuration,
raw samples and a report. Include conditions where whole-request arenas win.

Choose per-turn arenas only if their safety and measured results support the
intended workloads. A loss or inconclusive comparison is a legitimate result.
Do not generalize a single workload into universal throughput superiority.
