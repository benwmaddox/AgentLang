# Research: no language heap, program data stack only

Status: user-requested research and implementation direction, clarified
2026-10-08. The preferred model is immutable owning values in nearby arena
storage, with rare payload movement and compiler-controlled bulk reset.
Logical stack consumption need not immediately reclaim individual bytes.
Report 129's eager packing is measured evidence, not the intended final policy.

## Question and boundary

Can the language support its useful typed values and workloads using only a
program data stack with structured reclamation, without independently allocated
language objects having arbitrary lifetimes?

This is not the CPU call stack. The program data stack may live inside an arena,
and arena backing storage may be allocated by the host. Such backing allocation
does not by itself violate the proposed language model. Distinguish absence of a
language object heap from absence of every physical host allocation. Report host
metadata/compiler/provider allocations separately rather than hiding them.

Arenas are the general allocation rule. Specify what pushing, consuming,
duplicating, returning and retaining a compound value do to logical values and
physical storage separately. Ending a binding does not require compacting bytes.

## Preferred semantics: owning values

The user intends values, not references to independently lived language objects.
Payloads normally stay where constructed within a processing arena. Leave
interior dead bytes in place; rewind only a dead suffix or completed region:

The compiler must prove when a rewind is safe and inject the pointer update.
If provenance or escape analysis is uncertain, retain the data until a later
proved boundary. Do not use runtime liveness scans or reference counts to
decide. A runtime-sized saved mark is compatible with this static decision.

- An independent clone owns its complete nested payload. Ordinary immutable reuse
  may share its arena location internally without an observable reference feature.
- Logical ownership transfer need not physically relocate bytes. Reset must never
  invalidate a surviving value or pending provider operation.
- Returned results normally remain in the same processing arena as their caller;
  a function return is not automatically an allocation boundary.
- Retained mailbox state, queued messages and exported results own their values;
  they cannot retain pointers into a popped stack value.
- Named locals use bounded location metadata. Reading/reusing a local must not
  copy its payload merely to satisfy a contiguous operand-stack representation,
  or introduce a hidden shared heap or reference-counting lifetime scheme.
- Capacity failure must not partially move a value, publish half a result, leak
  owned payload, or invalidate the previous mailbox state.

Internal offsets may describe immutable payloads for their processing lifetime;
they cannot survive reset or create independently lived shared heap objects.
Only cross-lifetime retention requires transfer into an appropriate owned region.
Opaque external-resource capabilities have a separate resource-cleanup
contract; releasing value bytes alone does not acknowledge pending provider I/O.

### Physical locality

The user's Stasislang experience motivates actual payload locality. Consecutive
working values should occupy nearby physical address ranges, and nested fields
should be stored within their owner's contiguous representation. A dense stack
of handles to scattered allocations is not an acceptable implementation. Here
"physical" means actual program byte layout, not a guarantee that virtual pages
are contiguous in hardware RAM. Arena backing may use pages or bounded chunks.

Acceptance must inspect offsets, extents and stride, including alignment and
reserved frame/local space. Assert nested payload containment and disjoint
duplicate ownership, and report gaps/reservations separately from live bytes.
Later performance comparisons must exercise real traversal and transformation
patterns, including large values and field-wise scans; tighter packing does not
automatically improve every access pattern. Keep cache locality claims separate
from correctness and total-memory accounting.

Test scalars and variable-sized nested values, duplication, moves, returning a
non-topmost result, early errors, branch joins and repeated work under a fixed
capacity. After each logical pop or physical rewind assert live storage and validity of surviving
independent values. Poison/reuse freed storage to expose dangling aliases. Include
large value copies and transformations: report costs rather than selecting only
cheap scalar examples. Source notation and final physical layout remain open.

## Candidates to compare

1. Historical packed owning LIFO storage (report 129), retained as a measured
   control. Its eager copying/compaction is superseded by the user's clarification.
2. Preferred: typed values backed by a processing arena, with nearby payloads
   and bounded location metadata. Logical popping need not reclaim individual
   bytes; reclaim together at a safe boundary. No source borrowed references.
3. Nested processing arenas with explicit retained results, where analysis proves
   a useful safe boundary. Do not introduce one arena or copy per function call.
4. Sequential mailbox processing with a declared, bounded retained-data layout
   outside processing arenas, and an arena-backed data stack for all transient
   language values. This is the user's proposed static-data alternative, using
   Stasis as an analogy rather than adopting its implementation. No independent
   language object heap is available for other values. Define whether retained
   capacity is fixed at build time or selected once at startup; compare these
   variants separately from dynamically growing retained storage.

For candidate 4, handlers access typed retained state explicitly through their
inputs/outputs or declared host operations, rather than unrestricted mutable
globals. Retained fields and queue slots cannot hold references into a scratch
arena. Fixed-size values can be copied inline; strings, lists and nested records
need specified bounded representations, validated copies and structured capacity
failure. A fixed root holding an arbitrarily allocated object is not static-only
retention. Whole-arena retention is a separate candidate, not an unnoticed escape
from this restriction. Specify state/output publication on handler failure,
schema changes, reload, snapshots and rollback before claiming useful coverage.

For alternative controls, specify aliases and region-reference direction. For
the preferred model, enforce independent ownership. In both cases specify stack marks, lexical
locals, variable-sized strings/lists/records, Option/Result payloads, callbacks,
branch joins and multi-output returns. Measure copying, compaction or arena
transfer needed when useful results are not the newest allocations. Do not call
a model ownership-free if it merely hides lifetime tracking in the runtime.

## Acceptance and evidence

### Declared retained-state mailbox fixtures

For candidate 4, specify an inspectable retained layout with fixed customer
slots, bounded text/list payloads, and a bounded input/output queue. Keep the
capacity selected once for a run; report build-time and startup-selected
variants separately. The following are research acceptance cases, not existing
runtime features:

- Process repeated messages with nested transient values. After each handler,
  clear scratch and prove retained state and queued outputs remain valid, while
  used scratch storage returns to its baseline. Measure reserved capacity too.
- Attempt to retain a scratch reference inside a nested record, list, Option or
  Result. Reject the escape, or perform an explicitly specified bounded deep
  copy before publication; no payload may silently fall back to a language heap.
- Fill retained text/list capacity and queue capacity, then exceed each by one.
  Return a structured capacity error and verify the chosen publication contract
  leaves state/output consistent. Compare prepare-then-publish atomic updates
  with explicit partial-update semantics rather than assuming transactions.
- Delete and reuse a retained slot. If typed handles are allowed, an old handle
  must fail rather than resolve to a different object after slot reuse. Compare
  generation-checked handles with a copy-only retained representation.
- Fail midway through handling, reload code, restore a snapshot, and change the
  retained schema. Specify which state survives each boundary, and reject
  incompatible live state before activating the new schema.

Track queue/state storage, deep-copy bytes, unused fixed capacity, overflow
frequency and handler complexity alongside memory use. Include a workload whose
retained demand exceeds its declared capacity: bounded failure is correct, but
the frequency/cost determines whether this policy is useful for that workload.

Use identical semantic fixtures for nested values, repeated transforms, large
lists, early errors, returned results and repeated equal inputs with independent ownership. Include awkward
lifetimes, not only streaming scalar pipelines. Test that cleanup never leaves
usable dangling references and that capacity failure is structured and bounded.

Evaluate request/batch processing and the optional sequential-mailbox idea, but
also long-lived state, queues, caches, interactive retained results, dictionary
generations, hot replacement, snapshots and rollback. Preserve the PRD's
requirements or explicitly report workloads a strict model cannot support.
Host serialization is a possible boundary, not free or unmeasured persistence.

Measure used/reserved/peak bytes, growth across repeated work, allocation and
cleanup time, copy/compaction bytes and time, retained dead payload, runtime
complexity, and agent comprehension/error recovery. Compare against the managed
reference and shared/scoped arenas, while keeping the preferred owning-value
semantics explicit. Do not treat a faster reference-based control as completion
of the owning-stack design. Syntax
and LLVM adoption are separate variables; RPN is not required for this study.

Finish the early flow frontend first. Then specify a bounded prototype and
conformance cases; do not introduce stack/arena restrictions through undocumented
changes to current value semantics. See [the memory proposal](MEMORY-REGIONS.md).
