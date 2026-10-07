# Arena reset, reuse and watermark candidate

Status: proposed later runtime policy, recorded in response to the user's
question; no allocator implementation or performance result.

Recommended candidate: finish safe turn cleanup and logical reset promptly,
retain bounded backing chunks for reuse, and defer excess-capacity trimming
using high/low watermarks and an idle/decay policy. Arena-backed program-data
stack storage follows the turn lifetime; long-lived application state and
pending I/O buffers keep separate owners. Literal zeroing is distinct from
reset, and stale bytes must not be observable.

Added the candidate to the async experiment and roadmap. Test release/reuse/trim
policies separately from request-versus-turn lifetimes, under the same memory
ceiling and latency requirements. Include bursts, idle periods and large
outliers; track cached capacity, allocator calls and trimming/sanitization cost.

Existing mechanisms inform the proposal: [bumpalo reset](https://docs.rs/bumpalo/latest/bumpalo/struct.Bump.html#method.reset)
resets allocation state and returns excess chunks to its allocator;
[.NET Pipelines](https://learn.microsoft.com/en-us/dotnet/standard/io/pipelines)
uses distinct pause/resume thresholds to avoid rapid cycling;
[jemalloc](https://jemalloc.net/jemalloc.3.html) exposes unused-page decay.
These are references, not adopted dependencies or AgentLang benchmark evidence.

Validation: documentation links and local `git diff --check`. No runtime tests
were run for this design-only addition.

## User clarification: two retained-capacity levels

The user clarified that high/low refer to capacity kept after live usage falls,
rather than usage thresholds triggering trimming. Keep recent-peak capacity for
a bounded period, then a smaller warm reserve for the active mailbox; release or
pool unused reserve when it becomes inactive. Stack depth may change frequently
without backing-storage deallocation. Updated the roadmap and experiment contract
accordingly. Peak tracking, hold periods, reserve sizing and inactivity rules
remain to be specified and measured; global memory limits still apply.

This differs from the earlier Pipelines backpressure analogy: admission thresholds
and arena capacity retention are separate policies. It does not assume every
stack pop reclaims bump-allocated payloads. The clarification changes the research
candidate, not the current managed interpreter's allocation behavior.

## Latest revision: keep mailbox state, release idle stack capacity

The user subsequently simplified the candidate: keep mailbox static state and
clear stack storage after it has not been used for a while. The current roadmap
and async experiment now use persistent mailbox state plus idle stack-capacity
release, superseding the two-level retention policy above. Turn-local values
still end at safe turn boundaries; the timeout controls unused backing capacity.
Pending I/O buffers remain separately owned. Timeout duration and the events
that refresh it are benchmark parameters to define, not implemented behavior.
