# Async arena throughput experiment

Status: real-I/O throughput evaluation remains planned, 2026-10-08. A standalone
native ownership/memory comparison is complete in [report 104](../reports/104-native-arena-mailbox-feasibility.md):
72 runs passed, with lower per-turn backing for disposable working data and a
retained-only counterexample. [Report 106](../reports/106-native-record-ownership.md)
validates the native invocation-scratch/retained-output boundary.
[Report 107](../reports/107-native-state-reentry.md) adds typed retained state
re-entry across native invocations. [Report 108](../reports/108-native-mailbox-suspension.md)
validates host-driven suspension and scratch reuse with actual Flow/2 handlers.
[Report 126](../reports/126-native-mailbox-dispatch.md) validates bounded standalone
native dispatch of those handlers. The owning-value integration in
[report 131](../reports/131-owning-native-mailboxes.md) passes its focused native
lifecycle and full Release gates. Actual async I/O, saturation, production
throughput and tail latency remain untested. See [the roadmap](ROADMAP.md).

[Midori research](MIDORI-RESEARCH.md) supplies architectural comparisons, not
AgentLang performance evidence. Include bounded outstanding work, explicit
suspension, copy costs and retained-state interleaving in this evaluation.

## Product baseline and request/response workload

The primary runtime comparison is an equivalent idiomatic F# service, preserving
the same data contracts, request results, I/O behavior and admission limits.
The target is higher sustained successful throughput with comparable tail
latency and correctness, while allowing a modest whole-process RAM increase.
Before results, specify the allowed increase for the selected workload; it is
not yet a fixed percentage. Report both an equal-memory comparison and the
predeclared extra-memory point, plus the throughput/memory tradeoff. Do not infer
a win from throughput measured with unbounded queues or a silently larger budget.

Arenas are the general allocation model for language-managed dynamic values
for now. Request/response handlers are the first evaluation workload, not an
exceptional arena-only part of an otherwise general-heap language. Distinguish
working arenas, retained lifetimes, fixed/static state and host/provider storage;
do not assume one arena or one reset boundary for all values.

Start with request/response handlers and short-lived arena-backed owning values.
Exercise frequent cleanup on success, failure, cancellation and suspension.
Retain mailbox state separately; responses still being sent and pending provider
operations must keep valid owned storage until completion or acknowledged
cancellation. An async boundary is not permission to discard live data. Compare
the existing keep-associated and return-at-suspension candidates on those same
handlers. Arena reset/reuse and releasing backing to the OS are distinct events.

Keep deployment mode, warmup and instrumentation explicit: the interpreter is
not the final performance target, and development JIT and release AOT results
must be identified separately. Include F# runtime/GC and provider memory in the
whole-process measurements, just as AgentLang includes pools, metadata, retained
state and native/provider allocations.

Use Erlang/OTP to assess isolation and failure-containment behavior separately
from this performance result. Test that one handler's error, exhausted budget
or cancellation does not corrupt another mailbox's state. Document native-host
faults, external effects and other limits that are not contained; full OTP
supervision and distributed operation are not initial requirements.

## Candidates

### Compiler-guided sizing

User proposal, 2026-10-08: use a typed process-message entry function as a boundary
for estimating working-stack and pool capacity. Analyze reachable calls, local
layouts, branch maxima, returned values, construction/copy temporaries and
cleanup boundaries. Repeated work that reclaims its frame should contribute its
peak simultaneous storage, not the sum of every iteration's allocation.

Produce inspectable metadata with three distinct states: proven upper bound
under stated input/recursion limits, estimate under stated workload assumptions,
or unknown with the unbounded dependency identified. Input strings/collections,
recursive depth, retained accumulators and provider response sizes need bounds
before their estimates become guarantees. Keep resource configuration separate
from new source-level lifetime syntax. Link metadata to the actual backend/layout
revision; compare predictions with instrumented execution.

Pool capacity also depends on scheduling/admission configuration. A conservative
reservation is the sum, across pool classes, of slot count times slot capacity,
plus pool metadata and rounding. Returning stacks at suspension ties slot demand
to executing turns; keeping stacks attached also counts suspended owners. Retained
mailbox state, continuation storage, queue bytes and provider buffers remain
separate budgets. Source analysis alone cannot predict arrival rate or I/O latency.

Use estimates to suggest size classes and initial reservation, not as permission
to overrun storage. Enforce actual runtime bounds and defined exhaustion behavior.
Include atypical large messages, recursion limits, skewed handlers, slow I/O and
cancellation in prediction-error tests. Compiler-guided sizing is planned; the
first fixed-record ownership experiment does not implement a general analyzer.

### Lifetime policies

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

### Explicit comparison: keep or return the mailbox stack

User clarification, 2026-10-08: compare both policies using the same persistent
mailbox identity and static state. The variable is the lifetime and ownership
of its arena-backed program-data stack/scratch, not whether the mailbox survives.

| Policy | While async work is pending | On resumption/completion |
| --- | --- | --- |
| Keep associated | Keep the mailbox's actual stack/scratch arena and live owning values associated with that mailbox; do not return it to the shared pool. | Resume from the retained working data; reset/return the arena only when the request or declared operation group and all provider references have safely finished. |
| Return at suspension | Move the required state, continuation and provider buffers to explicit retained owners, then return scratch to a bounded pool. | Reacquire scratch for the same mailbox, resume, and release it again at the next safe boundary. |

The keep-associated implementation must retain real working data and avoid
copies that its lifetime policy makes unnecessary. Merely holding an empty,
already-reset arena while still performing all promotion copies would bias the
comparison. Both sides must implement equivalent language behavior, scheduling,
I/O, cancellation acknowledgements and admission limits. Neither policy may
reuse memory while a provider still holds a reference.

The next bounded mechanism check should retain the actual begin-result locations,
resume from those values in the same arena, and import only the new completion
message. Persistent mailbox state remains bank-owned between operation groups;
successful completion publishes it once before releasing scratch. A failed resume
or publication must preserve the pending prefix and token for retry. Verify the
existing stable emitter's descriptor-only operations and compiler-proven saved
marks before adding new ownership machinery. Two interleaved pending mailboxes
need distinct attached scratch slots. This check needs no new source lifetime
syntax and does not select a performance winner before real-I/O measurement.

Compare maximum sustained successful completions per second at the same enforced
memory ceiling and acceptable tail latency. Also record live and reserved arena
bytes, cached/pinned capacity, whole-process peak memory, copying bytes/time,
allocation/reset churn, pending work, rejection/backpressure and p50/p95/p99
latency. Include small/large working sets, small/large retained state, fast/slow
I/O, slow clients and cancellation. The earlier report104 logical-delay results
are preliminary mechanism evidence; the actual compiled-handler async comparison
is still required before selecting either memory policy.

The working hypothesis is that releasing scratch before I/O waits permits more
useful concurrent requests under a memory ceiling. Copying retained state and
additional transitions may instead limit CPU throughput. Measure both outcomes.

### Refinement: suspend and resume the same mailbox

The user proposed keeping all data that survives an async suspension in the
original mailbox's bounded retained state, returning its stack arena to the
pool, and acquiring stack storage again when the operation completes. The
mailbox identity and static state persist. No second mailbox is required for
processing the continuation; completion schedules resumption of the same one.

Continuation state includes required locals, nested data, the resume location
and pending-operation identity. I/O buffers must also have valid retained owners
before the provider can use them. Copy or transfer whole reachable values with
their nominal types intact; storing pointers into returned scratch is invalid.
Bounded promotion failure must leave a defined error/cleanup outcome, not a
partially published continuation. Data no longer needed need not be retained.

On suspension, complete the state transition and resource ownership checks
before returning the old arena. On completion, validate the pending operation
and schedule a turn on the same mailbox's execution lane with a new logical
arena lifetime. Pooling may reuse the same physical chunk; old references must
remain invalid. Define how cancellation, duplicate/late completion and code
replacement interact with the stored resume location before implementation.

Whether unrelated messages can execute in that mailbox during suspension is an
open scheduling decision. Single-thread execution alone does not define ordering
or protect invariants across intervening turns. Record the policy and test it.

Returning the stack arena to a pool at suspension differs from releasing its
backing memory to the OS. The pool may retain unused chunks for reuse and release
them after inactivity, subject to aggregate limits. Ordinary completed turns may
reuse assigned stack capacity under the idle-release candidate; async suspension
explicitly hands capacity back to the pool. The standalone probe implements
bounded pooling, but no idle-release policy. The compiled-handler controller
validates scratch reuse; a real async provider and both lifetime policies remain
to be integrated.

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

### Current candidate: persistent mailbox state and idle stack release

Separate logical lifetime end, backing-storage reuse, and returning capacity to
the allocator/OS. At a safe turn boundary, finish required resource cleanup,
invalidate old references and reset scratch allocation cursors and the
arena-backed program-data stack. Retain a bounded cache of reusable chunks.
Long-lived declared application state and pending I/O buffers are separate
owners; ending scratch does not reset them.

Literal byte zeroing is distinct from logical reset. New values must be fully
initialized before reads; stale bytes must not become observable. Specify any
required sanitization on reuse/export or isolation boundaries and measure its
cost. Do not assume clearing allocation cursors securely erases data.

The latest user revision replaces the two-level retention proposal:

- **Mailbox static state persists** for the mailbox's lifetime, independently
  of its turn-local stack. Access remains explicit typed state, not unrestricted
  mutable language globals.
- **Turn-local values end** at their safe lifetime boundary, while backing
  capacity can be reused for subsequent turns.
- **After stack inactivity**, release unused stack/scratch backing capacity or
  return it to a bounded shared pool. The next turn acquires capacity as needed;
  this does not clear the mailbox's persistent state.

Do not trim backing storage on each stack pop or trigger reclamation merely
because live usage crosses a level. Define the stack inactivity timeout and
which uses refresh it before benchmarking. An idle mailbox can still have live
pending operations; those buffers remain owned until safely relinquished. Values
and retained capacity are separately accounted. A resettable bump arena may
accumulate allocations until reset even if the operand stack has already shrunk;
strict LIFO allocation and bump scratch remain distinct candidates.

Cap aggregate retained capacity and handle unusually large chunks so one outlier
does not permanently inflate every mailbox's reserve. Memory pressure or the
process ceiling overrides a normal retention delay. Cached chunks count against
the memory budget; returning them to a host allocator need not immediately
reduce resident memory. Trimming never releases storage still needed by live
values or outstanding I/O.

Mailbox queue and pending-I/O watermarks govern admission/backpressure separately
from idle stack-capacity release. Limits include payload bytes as well as item
counts. Single-thread execution does not remove outstanding I/O references.
Do not reuse storage until all valid users have relinquished it; quarantine
awaiting buffers under a separately bounded owner if necessary.

After the first lifetime-policy comparison, test backing-storage policies as a
separate axis: immediate release, bounded reset-and-reuse, and bounded reuse
with idle-time release. Record inactivity rules before execution. Include burst,
idle, large-outlier and sustained-overload phases; measure allocator calls,
reset/sanitization/trim costs, cached bytes, whole-process memory and tail latency.
Keep the language's visible lifetime semantics identical across these policies.

Primary measurement: **maximum sustained successful completions per second at
the same enforced memory limit and acceptable tail latency**. Failed, rejected
and timed-out requests do not count as successful completions. A run outside
its latency or error budget is not a qualifying throughput result.

### Optional alternative: elastic mailbox pools by type

The user suggested considering a minimum/maximum instance count per mailbox
type, growing quickly with demand and retiring instances slowly after sustained
idleness. This is a research alternative, not an adopted language requirement.
Compare fixed capacity and elastic pools as a separate scheduling/capacity axis
after the arena lifetime and idle-release policies are understood.

Specify whether instances are interchangeable workers or distinct stateful
identities. The user's preferred candidate for distinct state sets `min = max`:
fixed mailbox instances with stable routing identities and retained static state.
Their unused stack backing capacity may still be released after inactivity;
fixed instance counts do not require permanently allocated scratch capacity.
Interchangeable workers remain the elastic-pool candidate. If future research
permits retirement of a distinct stateful activation, it requires an explicit
retained-state owner and identity-preserving routing contract first.
Scaling down must not discard mailbox static state, duplicate it into independent
writers, or lose queued/in-flight work. Admission stops before safe draining and
retirement. Pending operations and late completions retain defined destinations.

Define per-type min/max counts, growth signals, downscale hold time, fairness,
byte budgets and behavior at maximum capacity before benchmarking. Per-type
counts do not replace global memory and pending-I/O limits. A minimum can be a
warm activation target rather than a requirement to allocate every arena's full
capacity in advance; make the chosen interpretation explicit.

More mailboxes on one execution thread provide additional routing/pending-work
capacity, not additional CPU execution parallelism. Whether that hides I/O waits
and improves successful throughput must be measured. Keep whole-process versus
per-mailbox single-thread scheduling fixed during each comparison.

Record cold-start and retirement costs, active/idle counts by type, queue age,
backpressure, retained state and whole-process memory. Include burst/idle cycles,
hot stateful identities, late I/O and skewed demand between types. Compare with
the simpler persistent-mailbox/idle-stack-release candidate without assuming
elasticity is superior.

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
