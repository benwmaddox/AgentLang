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

First finish the expression/dot frontend and maintain runtime value bounds. Then prototype one bounded scratch-region use with observable accounting before exposing general region operations. Before native implementation, define escaped return, nested-container, failed promotion, hot replacement, rollback/snapshot and resource-cleanup conformance cases.

Measure allocation throughput, cleanup time, used/reserved/peak memory, promotion cost and amount, repeated-evaluation growth, and long-lived mixed-lifetime workloads. Include cases where arenas retain dead intermediates; do not benchmark only phases favorable to bulk reclamation. Adopt region policies based on correct semantics and measured benefits. LLVM remains conditional later work; arenas are recorded as a proposed allocation direction, not current performance evidence.
