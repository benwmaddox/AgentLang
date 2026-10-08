# Stable arena lowering

Status: implemented in the bounded owning backend, 2026-10-08; focused native
acceptance and the full local Release gate passed. See [report 130](../reports/130-stable-arena-rewinds.md).
This replaces the packed placement policy measured in
[report 129](../reports/129-variable-owning-values.md). The semantic IR, immutable
source values and inline record layout remain authoritative.

## Allocation and lifetime

Allocate payloads sequentially in bounded arena storage. Ordinary binding,
read-only local access, field projection and same-arena call/return carry small
location descriptors rather than copying payloads. A descriptor contains a
checked offset, extent, logical payload size and the end of the backing owner.
Type information comes from verified IR. These are internal compiler/runtime
details, not source references or a separately allocated object graph.

Keep records inline with their nested payloads. Constructing a new record from
existing values may copy fields into that representation; count these bytes as
construction, not free ownership transfer. Destination-directed construction is
a later optimization, not a reason to substitute shared record graphs now.
Explicit independent duplication and external retention may also copy.

The compiler proves rewind safety from value provenance, scopes and outgoing
values. Emit a saved mark and rewind only where that proof establishes that
every allocation after the mark is dead. An escaping field keeps its backing
owner live. Unknown provenance, uncertain calls or a branch that can retain an
allocation prevent that rewind. Keep growing within the configured capacity
until an enclosing cleanup boundary is proved safe.

Do not emit runtime root scans, reference counts, highest-live-address searches
or conditional liveness checks to decide whether reclaiming storage is safe.
Saved marks may contain runtime byte offsets because value lengths vary; loading
a saved mark and assigning the bump pointer executes a compile-time decision.
Ordinary capacity/bounds checks and debug assertions are separate safeguards,
not a replacement for the compiler's lifetime proof.

For example, if a scope allocates temporary data followed by an escaping result,
the temporary bytes below the result stay reserved. Do not move the result to
recover them. A proved dead-only scope can rewind immediately. Same-arena
function returns retain their allocations when results may depend on them.
Conservative interprocedural summaries may prevent early rewind; missing proof
must never cause a runtime guess or introduce a user lifetime annotation.

At request/processing completion, serialize or transfer outputs into appropriate
retained storage, verify no pending operation still uses scratch, then reset to
the entry mark or return the arena to a pool. Arena reset does not roll back
external effects or release an outstanding I/O provider's ownership.

No runtime reference counting, allocation liveness graph or per-object free is
required for this compiler-controlled policy. Unused interior space is deliberate.
Do not add eager per-operation reclamation before proving a need for it.

Explicit compaction is deferred research, not part of this implementation.
The suggested `Allocation.drop(temporary)` spelling is not a committed API.
Logical dropping and physical relocation must remain distinct. Consider a
relocation mechanism only after measuring retained dead space, and only with
compiler-proven control of all affected locations and external dependencies.

The user's subsequent refinement is an opt-in compaction point after a deep
computation: retain its few surviving results at the region's saved mark, then
rewind past those results to recover the intervening dead allocations. Prefer
evaluating a structured region whose outputs identify the survivors over dropping
one arbitrary temporary. The user prefers this candidate form for future work:

```text
let summary = compact {
    let raw = loadData()
    let processed = analyze(raw)
    summarize(processed)
}
```

This is planned syntax, not an available language feature. A record or tuple
could carry multiple surviving results. Being at the logical stack top
alone is insufficient: the compiler must prove that the reclaimed region has no
other live dependents, including projected fields and pending I/O. Relocation
must preserve complete nested/variable-sized results and update every affected
internal descriptor. If proof is unavailable, an explicit reclaiming operation
must fail validation rather than guess at runtime. A runtime size threshold may
choose whether a proved-safe move is worthwhile; it cannot establish safety.
Ordinary scope exits, function returns and logical drops still never compact.
Compare bytes recovered with bytes moved and latency before adopting this option.

## Implementation boundary

Replace the implicit contiguous operand-payload suffix in
`src/AgentLang.Llvm/OwningStackAot.fs` with explicit locations. Update locals,
branch joins, calls, results and native wrapper together. The current fixed and
dynamic emitters must not silently select different lifetime policies based on
whether a String happens to be present; use the stable policy for both.

Keep the checked native layout/UTF-16 routines and inline serialization. Audit
context marshaling, generated LLVM structures, C layouts and fixture oracles if
their shapes change. This prototype does not need compatibility adapters or
routine version increments. Retain historical source/evidence snapshots.

Failure unwinds must preserve the first structured error, restore the processing
entry mark and leave the caller's retained output unchanged. Every descriptor
must remain within initialized arena storage until its logical lifetime ends.
No descriptor may escape a reset into retained state.

## Acceptance

- Six-local cleanup produces zero payload copies/moves and a dead-only scope
  returns to its mark.
- Binding, repeated local reads, calls and returns preserve payload addresses.
- An escaping result retains temporary bytes beneath it; nested scope exit
  never poisons that result or a projected field's backing owner.
- Inspect generated IR: proved dead-only scopes contain mark rewinds; escaped
  or uncertain scopes do not. No runtime liveness decision authorizes a rewind.
- Branch joins, same-slot shadowing, larger/multiple results, zero-output calls,
  explicit independent duplication, Unicode and layout-depth controls preserve
  interpreter parity at LLVM O0/O2.
- Repeated completed processing regions reuse capacity without accumulation.
  Capacity errors, malformed input and failures before output publication leave
  retained output unchanged.
- Construction, input staging, independent cloning and retained-output copies
  are reported separately from descriptor traffic and payload relocation.

Live-byte instrumentation must not double-count overlapping descriptors. Report
logical root bytes separately from unique live payload, occupied cursor, dead
interior space, reserved capacity and metadata. Exact overlap accounting may be
diagnostic-only; do not impose a collector merely to obtain a counter.

Run the fresh focused `scripts/Verify-NativeValueStack.ps1` gate, native strict C
and sanitizer-trap checks, applicable formatting, and the full local
`scripts/Validate.ps1 -SerialBuild -SkipPackageAudit` Release gate before push.
Compare with frozen report 129 at equal and sufficient capacities; different
exhaustion points are expected. Byte counters alone do not establish throughput,
whole-process memory or agent-edit reliability improvements.

Tiny exact-capacity fixtures are correctness controls, not service sizing
targets. The user accepts modest retained-space growth to avoid movement.
Application evaluation must use representative request sizes and concurrency,
including slow I/O, with throughput, tail latency and whole-process memory
measured together. Do not optimize away a few fixture bytes at the expense of
the intended stable-placement policy.
