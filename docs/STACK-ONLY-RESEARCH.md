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

Specify allowed aliases and region-reference direction, stack marks, lexical
locals, variable-sized strings/lists/records, Option/Result payloads, callbacks,
branch joins and multi-output returns. Measure copying, compaction or arena
transfer needed when useful results are not the newest allocations. Do not call
a model ownership-free if it merely hides lifetime tracking in the runtime.

## Acceptance and evidence

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
