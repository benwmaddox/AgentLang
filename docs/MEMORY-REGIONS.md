# Proposed scoped arenas and retained memory

Status: proposed memory design, 2026-10-05. No arena allocator or region lifetime checking is implemented. Preserve the current managed runtime while completing the flow frontend; settle lifetime/conformance rules before any native allocator backend.

## Objective

Use arenas for phase-oriented values that can be reclaimed together. This can reduce allocation/reclamation overhead and make temporary lifetimes explicit. It does not by itself minimize peak memory: dead intermediate objects may remain allocated until a region ends, promotion can copy data, and reset may retain backing capacity. Measure used, reserved and process memory separately.

The user clarified that easier arena cleanup at reasonable boundaries was part of the original appeal of Forth syntax. Preserve this lifetime discipline through the frontend change: recent values, explicit inputs/outputs, limited hidden roots, and inspectable phase boundaries. Dot-flow source must not lose that objective merely because it adds names. The semantic IR should support analysis of value uses and escapes before a region policy is selected. An operand-stack pop alone is not proof of dead storage because locals, returned containers and retained state can still refer to the same value.

| Candidate region | Lifetime and retained boundary |
| --- | --- |
| Compiler scratch | Parse/type/lowering attempt; accepted source and verified IR must acquire longer-lived ownership before scratch reset |
| Test evaluation | One isolated case; results/diagnostics/coverage exported before reset, provider resources cleaned up separately |
| Evaluation scratch | Temporary intermediate values; returned values must be copied/promoted/transferred to a valid longer-lived owner |
| Dictionary/program generation | Accepted definitions/constants/IR; retain while active execution, task rollback or snapshots still reference the generation |
| Application/project state | Explicitly retained typed data; not automatically dumped at every word return or task completion |

These are candidate boundaries, not a declaration that every task or word owns one arena. Words routinely return values to callers. A per-word reset that invalidates those values would violate language semantics.

## Required safety contract

### What Forth supplies, and what we would add

Standard Forth's operand-stack discipline does not establish ownership of
referenced storage. [`DROP`](https://forth-standard.org/standard/core/DROP)
removes a stack item; dropping an address does not free its allocation. The
optional [memory-allocation word set](https://forth-standard.org/standard/memory)
provides `ALLOCATE`, `RESIZE` and `FREE`, and permits access only while the
allocated region remains live. The program is responsible for obeying that
restriction; this is not a standard static lifetime/escape checker.

For inline scalar values, stack-space reuse needs no heap lifetime analysis.
The harder case is an address or reference copied into another live stack item,
local, container or retained state. A manually reset arena is safe only if all
remaining references obey its lifetime, whether the programmer establishes
that fact or the language enforces it.

The design requirement is therefore **enforced lifetime safety**, not a mandate
for one analysis algorithm or a Rust-style annotation system. Candidate
mechanisms include conservative scoped-region types, ownership transfer,
bounded copying of escaping results, and checked handles at dynamic boundaries.
Rejecting an unsupported escape is preferable to inferring an unsafe lifetime.
Any automatic promotion must be specified, inspectable and accounted for;
silently copying arbitrary retained graphs would undermine predictable cost.

The simplest first region experiment should allow short-lived internal values,
permit borrowing longer-lived immutable inputs, and require a valid longer-lived
owner for exported results before reset. Test nested aliases, early errors and
retained results, not only scalar pipelines. This remains a proposal; lexical
scope in the new frontend does not itself allocate or reset an arena.

1. No reference may remain usable after its backing region is reset/released. Use static escape/lifetime checking where tractable and checked opaque handles where dynamic boundaries require it; do not expose raw pointers or unchecked lifetime casts to programs.
2. Promotion/copy must recursively preserve nominal identities, closed container types, value contents and graph sharing where applicable. Moving only an outer record while leaving its fields in a shorter-lived arena is invalid. Preserve validated value semantics; do not rebind a frozen type validator.
3. Promotion must be bounded and fail with a structured error. Account for copied bytes/nodes separately from execution fuel and do not publish a partial retained graph after failure.
4. Old executable generations remain valid until no active frame, task rollback state or retained snapshot requires them. Hot replacement must not release a region merely because its word is no longer current.
5. Logs, source/history, diagnostic strings and protocol results own valid memory after the operation region ends. Serialization/export completes before scratch release or transfers ownership explicitly.
6. Memory reclamation is not resource cleanup or effect rollback. Files, database resources and future handles need explicit host-managed cleanup on normal/error/abort paths. Arena reset does not reverse external writes.
7. Capacity checks cover actual allocations, padding, backing chunks and retained roots. Reset/reuse and release are distinct operations; expose truthful measurements rather than reporting capacity as live payload or promising immediate OS memory return.
8. Syntax, stack position and binding-locality lint do not select memory lifetime implicitly. Test/temporary word status is vocabulary lifetime; library maturity is quality. Neither is a value-allocation region.

The reference [bumpalo documentation](https://docs.rs/bumpalo/latest/bumpalo/) describes phase-oriented bump allocation and bulk reclamation, and also the lack of automatic destructor calls. Its [reset API](https://docs.rs/bumpalo/latest/bumpalo/struct.Bump.html#method.reset) returns excess chunks to its allocator while allowing reuse of remaining capacity. These illustrate decisions our runtime must specify, not an adopted dependency or universal allocator behavior.

## Managed and native implementations

The current F#/.NET values use managed objects. Dropping a set of roots makes unreachable objects eligible for collection; it does not bulk-free arbitrary managed objects immediately. See [.NET managed memory](https://learn.microsoft.com/en-us/dotnet/standard/garbage-collection/fundamentals). Pooling or unmanaged buffers are different implementation choices and require their own safety contracts. Do not claim that a managed arena-shaped API establishes native arena allocation.

A later LLVM backend can use concrete region allocations for eligible data, while persistent data uses a separately specified retention strategy. F# may remain the compiler/host. Interpreter/JIT/AOT must agree on values, nominal validation, errors, effects and lifetime-visible behavior. Physical allocation is backend-specific unless an explicit language feature makes a region observable. Do not add allocator instructions to authoritative semantic IR before those semantics are settled.

## Evaluation and sequencing

### Candidate: arena-only language allocation

The user raised restricting language values to a data-model stack plus arenas,
without independently freed heap objects. This is a research alternative, not
an adopted change to V1. The physical host can still allocate arena chunks;
the restriction concerns the language's retention and reclamation model.

One coherent candidate gives each processing phase a region shared by its word
calls, forbids retained references from an older region into a newer one, and
permits exporting only inline values or bounded copies/transfers into a valid
owner. This reduces lifetime bookkeeping to region boundaries rather than
per-object reclamation, but still requires enforcement of the reference rule.
Strict value-only copying can avoid general alias analysis at a measurable copy
cost. A purely consumptive scalar stack is simpler; variable-sized nested data
and returned collections require an explicit storage policy.

Huge arenas do not establish bounded working memory: repeated short-lived
allocations remain charged until reset, and a live operand stack can be tiny
while its region grows. Evaluate nested iteration/request regions, bounded
allocation failure, retained outputs, state replacement, caches and interactive
sessions. Persistent dictionaries/history/snapshots and long-lived application
state need separate retained owners or host serialization; they cannot point
into released evaluation scratch. Do not silently remove these PRD requirements
to make an arena-only experiment pass.

First finish the expression/dot frontend and maintain runtime value bounds. Then prototype one bounded scratch-region use with observable accounting before exposing general region operations. Before native implementation, define escaped return, nested-container, failed promotion, hot replacement, rollback/snapshot and resource-cleanup conformance cases.

### Candidate: single-threaded mailbox processing

The user proposed a mailbox with input/output and one arena per processed item,
including requests, with single-threaded execution enforced. This is an optional
thought, not a requirement or a change to V1's synchronous execution model.
Evaluate it as a sequential host scheduling boundary before adding a language
actor system or asynchronous semantics.

A candidate lifecycle dequeues one retained input, invokes one handler with
isolated scratch, validates/prepares its retained outputs, publishes those
outputs, and then releases scratch on success or failure. Queue payloads,
published responses and any mailbox state must own storage independent of the
released scratch arena. Bounded copying/serialization is one policy; whole-arena
transfer is another, with the cost of retaining unused intermediates. Nested
word calls share the item region rather than creating independently reclaimable
regions by default.

Single-threading simplifies scheduling and removes concurrent mutation from
this candidate, but it does not make queued references safe or reverse external
effects. Require bounded queue count and payload bytes, explicit overflow/back
pressure, a defined failed-item outcome, and a policy for output publication and
state updates. A handler that synchronously waits for another mailbox handler
cannot make progress under a strict non-reentrant single-handler scheduler;
prefer publishing a follow-up item or returning a structured result. No retries,
exactly-once delivery or transactional external effects are implied.

The simplest experiment is stateless input-to-output handlers with one active
item at a time. Stateful handlers need an additional retained-state ownership
policy, measured separately from scratch reclamation. Inspectable mailbox/handler
signatures, declared effects and per-item allocation metrics would preserve the
project's existing comprehension and observability goals.

Measure allocation throughput, cleanup time, used/reserved/peak memory, promotion cost and amount, repeated-evaluation growth, and long-lived mixed-lifetime workloads. Include cases where arenas retain dead intermediates; do not benchmark only phases favorable to bulk reclamation. Adopt region policies based on correct semantics and measured benefits. LLVM remains conditional later work; arenas are recorded as a proposed allocation direction, not current performance evidence.
