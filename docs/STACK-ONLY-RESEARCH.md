# Research: no language heap, program data stack only

Status: user-requested research track, 2026-10-05. This is not an adopted V1
allocation restriction or an implemented allocator.

## Question and boundary

Can the language support its useful typed values and workloads using only a
program data stack with structured reclamation, without independently allocated
language objects having arbitrary lifetimes?

This is not the CPU call stack. The program data stack may live inside an arena,
and arena backing storage may be allocated by the host. Such backing allocation
does not by itself violate the proposed language model. Distinguish absence of a
language object heap from absence of every physical host allocation. Report host
metadata/compiler/provider allocations separately rather than hiding them.

Keep this distinct from general arena-only allocation: an arena can contain
arbitrarily connected values with a common lifetime without enforcing a stack.
The strict candidate must specify what pushing, consuming, duplicating,
returning and retaining a compound value do to both data and storage.

## Candidates to compare

1. A strict LIFO value stack, optionally arena-backed. Compound values own their
   nested payload; duplication copies or uses a specifically safe scoped view.
   No arbitrary references into storage that can be popped/reset independently.
2. A stack of typed values backed by one processing arena. Popping values need
   not reclaim individual payloads; reclaim together at a phase boundary.
3. Nested processing arenas with explicit retained results. This relaxes strict
   stack-only retention and must be reported as a different candidate.
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

Specify allowed aliases and region-reference direction, stack marks, lexical
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
lists, early errors, returned results and shared inputs. Include awkward
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
reference and scoped arenas before selecting a default memory policy. Syntax
and LLVM adoption are separate variables; RPN is not required for this study.

Finish the early flow frontend first. Then specify a bounded prototype and
conformance cases; do not introduce stack/arena restrictions through undocumented
changes to current value semantics. See [the memory proposal](MEMORY-REGIONS.md).
