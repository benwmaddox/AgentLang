# Native arena and mailbox ownership comparison

Status: implementation and validation in progress, 2026-10-07. This is a
standalone native experiment following scalar LLVM conformance (report 103),
not an arena allocator integrated into AgentLang.

## Question and scope

Can a single execution lane suspend a mailbox, retain only the data needed for
resumption and provider access, and safely reuse its scratch backing storage?
Compare that candidate with retaining scratch for the whole request. Measure
real native allocation, copying, cleanup and process memory before changing the
language's memory semantics.

The C11 probe uses explicit bounded structs and Win32 allocation, with no new
package dependencies. C is an experiment implementation choice, not a decision
about the eventual production runtime. The existing F# frontend, verified IR,
interpreter and LLVM backend remain unchanged.

## Fixed comparison contract

Before collecting results, fix the following matrix:

- Clang 19.1.5, fresh builds at O0 and O2.
- Per-turn and whole-request modes, alternating their order by repeat.
- Six workloads: CPU control, delayed provider with small continuation,
  delayed provider with large continuation, slow outgoing consumer, cancellation,
  and a retained-only control with no disposable intermediate working data.
- Three repetitions, 32 offered requests, seed 17: 72 matched runs.
- Sixteen KiB native chunks and a one MiB shared cache cap for both policies.
  The first five workloads allocate and consume 512 KiB of disposable scratch;
  retained-only keeps the large graph but omits that disposable working set.
- Eight MiB application allocation budget, including fixed state/metadata and
  cached arena backing; 64 MiB whole-process committed-memory ceiling.
- One execution thread and fixed mailbox identities. A suspended mailbox queues
  unrelated messages FIFO; completion/cancellation controls resume the same one.
- One pending operation per mailbox. Buffers stay owned until the provider or
  outgoing consumer acknowledges release. A cancel request alone releases none.

Review before measurement added the retained-only control and explicit disposable
scratch: the original graph-only shape would measure copying retained data but
would not test releasing dead intermediates. The shared cache holds a complete
working set instead of forcing nearly all pages to be freed every turn.

The provider uses deterministic logical delays. Logical ticks are not network
latency, and elapsed probe time is not saturated-server throughput. Memory
capacity rejections are observable outcomes, not successful completions.

The native program enforces the process ceiling using a Windows Job Object and
verifies it by rejecting an over-limit allocation. The separate arena budget
counts backing chunks even while cached. Actual committed/reserved bytes and
used payload are different quantities. Pages are touched, and process memory is
measured independently. See Microsoft's documentation for
[VirtualAlloc](https://learn.microsoft.com/en-us/windows/win32/api/memoryapi/nf-memoryapi-virtualalloc),
[job memory limits](https://learn.microsoft.com/en-us/windows/win32/api/winnt/ns-winnt-jobobject_extended_limit_information)
and [process memory counters](https://learn.microsoft.com/en-us/windows/win32/api/psapi/ns-psapi-process_memory_counters_ex).

## Acceptance

Safety checks cover generation-checked handles after physical reuse, bounded
nested promotion with aliases and nominal tags, atomic failed promotion,
retained output after scratch reset, queue/pending/output capacity, cancellation
before/after readiness, duplicate/late completion, failure cleanup and repeated
reuse. Draining must release all dynamic owners and backing allocations while
preserving declared mailbox state.

An independent PowerShell checker verifies every offered ID, terminal status,
completed value and resource invariant. The completed-value oracle is unsigned
32-bit `(seed * 1000003 + (id + 1) * 97 + 0x41C64E6D)`; the native handler must
read and validate its retained graph to produce that value. Every non-cancellation workload must complete all 32 requests; cancellation
must cancel exactly the 11 IDs divisible by three and complete the other 21.
Every mailbox is `id % 8`. Capacity rejection is tested separately and cannot
substitute for success in this non-pressure matrix. Completed values must agree
across modes; identical configurations must preserve outcomes and counters
across optimization and repetition.
Checks reject missing IDs, inconsistent counts, wrong values, unreturned owners,
false process-limit enforcement and budget violations. All native build/run
outputs and hashes will accompany the results.

## What this cannot establish

Checked handles in a trusted C probe do not establish language-wide escape
analysis, general nominal/container promotion, strict LIFO allocation or an
arena-backed AgentLang data stack. The prototype fixes handler version and does
not test dictionary replacement or suspended-code generation retention.

The comparison is not a web server and has no real asynchronous I/O. A later
throughput decision still needs an external scheduled-arrival load generator,
real provider/slow-client behavior, equal process ceilings, predeclared latency
and error budgets, saturation/overload sweeps and repeated qualifying results.
It also does not alter the efficacy conclusion: agent composition and repair
are feasible, but comparative reliability superiority remains unproven.
