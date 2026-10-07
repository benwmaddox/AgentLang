# Shared semantic IR and later native backends

This is the architecture contract. Source lowering, IR verification, a standalone interpreter, and structured inspection are implemented. The public Runtime now executes verified IR through the interpreter; cutover validation is recorded in report 010. An optional scalar LLVM AOT backend is locally validated; see [report 103](../reports/103-llvm-architecture-and-native-slice.md). JIT and general native release support remain future work.

Source → parsed AST → resolved/type-and-effect-checked definitions → typed semantic IR → interpreter.

The IR records exact input/output stack types, local types, resolved stable word identities, declared/transitive effects, constants, structured control flow, and source spans. Record construction/field access, nominal scalar validation, closed containers, exhaustive cases, and static collection callbacks must have explicit operations or explicit checked calls. Keep it small and printable. Metadata and authoring source remain available separately.

The first lowering milestone preserves existing semantics and protocol behavior. Run all current acceptance tests against the IR interpreter, plus focused tests for stack joins, locals, error spans, nominal identities, callback effects, and coverage mapping. Branch/outcome coverage must continue to identify source-level obligations after lowering. Runtime errors remain structured; host exceptions cannot become successful error expectations.

Secondary execution target, following the efficacy review:

| Environment | Execution and shipped components |
| --- | --- |
| Development | Interpreter, REPL, introspection, hot replacement, compiler; LLVM JIT for selected stable/frequent words |
| Release | LLVM AOT native code and necessary minimal runtime; no interpreter or compiler |

Define conformance fixtures before adding native backends. They must agree on Int overflow, finite Float policy, nominal validation, errors, effect authorization, container limits, evaluation order, and observable results. Native optimization cannot remove or reorder observable effects contrary to those semantics.

Development calls resolve through versioned word identities. Hot replacement must invalidate compiled callers or route them through an updated dispatch boundary; stale compiled bodies must not silently retain replaced behavior. Pin the release dependency graph and reject unresolved candidates/temporary words before AOT.

The dictionary provides controlled late binding of implementations, not dynamic
name/type guessing. Resolve identities, signatures and effect contracts before
execution. For a scoped test override, invalidate the transitive compiled caller
closure, including inlined/specialized dependencies and static callbacks. The
initial safe policy is to interpret that affected closure against the test's
dictionary overlay for its duration. Other unaffected compiled functions may
remain native. On cleanup, remove the overlay and restore or rebuild dispatch
against the original generation; do not reuse code compiled for the mock.
Retain active executable generations until their frames have finished. Ordinary
definitions/history stay unchanged. Release AOT pins implementations and may
use direct calls/inlining without requiring a live development dictionary.

The scalar AOT slice introduces a versioned Windows x64 ABI and direct linking. Container layout, allocation strategy, debug information, JIT integration and native performance targets remain later design work. Require explicit versions and deterministic layout oracles when those contracts are introduced. Do not make LLVM a dependency of the initial runtime or agent pilot.

Priority update, 2026-10-07: the efficacy report remains first. Efficient LLVM
execution (development interpreter + JIT, release AOT) and a chosen memory design
are second, rather than waiting for every optional language feature. A comparative
reliability advantage is not a prerequisite for a bounded native prototype.
Start with a small semantic-conformance slice and measured arena lifetime/pooling
experiment. Per-turn scratch with explicit retained mailbox state is the main
candidate; compare whole-request arenas before adopting it. General native
release support still requires complete semantics and checked lifetime/ABI rules.

The proposed allocation direction includes [scoped arenas](MEMORY-REGIONS.md) for phase-oriented values and a distinct retained-data strategy. Define escape/promotion and old-generation retention before resetting a region. Bulk reclamation must not invalidate returned values, snapshots or active code, and does not replace resource cleanup. The managed interpreter and eventual native backends share value/lifetime semantics without a promise of identical physical allocation. No arena implementation or memory gain is claimed yet.

Later syntax research may introduce a frontend with named inputs, expression notation, or pipelines while retaining local flow through recently produced values. All frontends must lower to this same typed semantic IR and preserve evaluation order, diagnostics, effects, and source-level coverage obligations. Source notation and backend memory behavior are separate experiments. LLVM is an execution/code-generation backend, not necessarily a replacement for the F# host/compiler implementation; lower memory usage requires measured allocation and value-layout choices. See [the late syntax research item](PRD.md#late-research-syntax-and-stack-locality).

Implementation-language flexibility (2026-10-07): F# is the current host, not a
required architecture choice. Prefer the implementation that is correct,
maintainable and effective for its role. Retain the tested F# frontend/type/IR
pipeline for the initial LLVM backend; native emitted code does not require a
managed runtime. Select the later native runtime language on explicit allocator,
mailbox, interoperability and maintenance needs while preserving semantic and
ABI conformance. No full host rewrite is required by the native target.

## Scalar native API boundary

`AgentLang.Llvm` compiles an empty-input `VerifiedIrBody` and its reachable
verified dictionary snapshot. The initial target is Windows x64 with Int, Bool
and Unit values. Unsupported types, operations and effects fail before tool
invocation; there is no interpreter fallback within a compiled program.

The host supplies frozen `NativeDiagnosticSources` from the same lowering
context, preserving the definition spans used by interpreter diagnostics.
Compile-time metadata and runtime error arguments reconstruct the structured
language error in the host wrapper. The emitted DLL returns a status and
metadata identifier; it does not unwind a managed exception across the ABI.
The diagnostic metadata currently lives in the compiler-side artifact handle,
so this API is not yet a standalone release packaging format.

ABI v1 uses a 32-byte context, 8-byte scalar slots and a 4-byte status. Nested
calls share caller-owned scratch output capacity; logical output count is a
separate value. This scratch buffer is not the proposed arena allocator.
The exact contract and independent layout oracle are in
[`abi-v1.json`](../tests/fixtures/native-conformance/abi-v1.json).
Run the optional local native gate documented in the README; compiling the
ordinary solution does not invoke LLVM tools.
