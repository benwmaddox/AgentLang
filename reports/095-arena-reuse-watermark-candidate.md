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
