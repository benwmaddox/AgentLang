# Shared semantic IR and later native backends

This is the architecture contract. Source lowering, IR verification, a standalone interpreter, and structured inspection are implemented. The public Runtime executes verified IR through the interpreter; cutover validation is recorded in report 010. Native prototypes include the earlier graph-backed `LlvmAot` and the selected stable-arena `OwningStackAot` direction. Follow [stable arena lowering](STABLE-ARENA-LOWERING.md) for current memory semantics. JIT and general native release support remain future work.

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

Early native experiments used [scoped arenas](MEMORY-REGIONS.md) for phase-oriented values and a distinct retained-data strategy. The selected owning backend now follows the stable-arena contract linked above. Define escape/promotion and old-generation retention before resetting a region. Bulk reclamation must not invalidate returned values, snapshots or active code, and does not replace resource cleanup. The managed interpreter and eventual native backends share value/lifetime semantics without a promise of identical physical allocation. A standalone native arena/mailbox experiment measured bounded memory behavior in [report 104](../reports/104-native-arena-mailbox-feasibility.md); native record execution now has an invocation-scratch and retained-output boundary ([report 106](../reports/106-native-record-ownership.md)). Source-defined suspension handlers first ran under a managed experiment host ([report 108](../reports/108-native-mailbox-suspension.md)); the bounded native controller and module boundary are described in [report 126](../reports/126-native-mailbox-dispatch.md). The later owning backend tests real I/O and cancellation in [report 133](../reports/133-real-io-mailbox-correctness.md); that bounded result is not general release certification.

Flow/2 already provides named inputs, expression notation and receiver-style composition. Further syntax research can evaluate how well those forms preserve local data flow. All frontends must lower to this same typed semantic IR and preserve evaluation order, diagnostics, effects, and source-level coverage obligations. Source notation and backend memory behavior are separate experiments. LLVM is an execution/code-generation backend, not necessarily a replacement for the F# host/compiler implementation; lower memory usage requires measured allocation and value-layout choices. See [the late syntax research item](PRD.md#late-research-syntax-and-stack-locality).

Implementation-language flexibility (2026-10-07): F# is the current host, not a
required architecture choice. Prefer the implementation that is correct,
maintainable and effective for its role. Retain the tested F# frontend/type/IR
pipeline for the initial LLVM backend; native emitted code does not require a
managed runtime. Select the later native runtime language on explicit allocator,
mailbox, interoperability and maintenance needs while preserving semantic and
ABI conformance. No full host rewrite is required by the native target.

## Selected owning backend

`OwningStackAot` keeps values inline in bump-allocated arenas and uses compiler
provenance to authorize rewinds. Ordinary local access, projection and same-arena
calls transfer location metadata; they do not relocate payloads. Uncertain
lifetimes retain storage until a later safe boundary. This is the target for
new native structural-type support; the graph API below is an earlier prototype.

The closed-enum extension is tracked in [report 153](../reports/153-owning-native-enums.md).
Enums use an eight-byte ordinal and an existing nominal type identity. Layout
ABI 2 appends a case count to each type descriptor (36 bytes); Stack ABI 1,
context/event layouts and the 40-byte layout struct remain unchanged. Native
ingress checks every input, including ignored values and nested enum fields.
Existing conservative lifetime analysis may retain more space around enum
operations; no new runtime liveness decision is introduced.

The active stable-arena emitter handles both fixed-size and String-bearing
layouts. The historical fixed emitter is inactive. Native Option/Result payload
layouts remain a separate extension. Generic mailbox/bank layout consumers use
the shared validator, while handwritten application hosts enforce their schemas.

Mailbox manifest format 1 permits additive inspection fields. It now exposes
`layoutAbiVersion` and `typeDescriptorSizeBytes`, plus per-type `caseCount`.
Module callback ABI 1 and managed execution-result schema 2 are separate,
unchanged contracts. Consumers must check the physical layout ABI before reading
native descriptors; the old 32-byte layout cannot be interpreted as ABI 2.

## Earlier graph-backed native value API boundary

`AgentLang.Llvm` compiles a typed `VerifiedIrBody` and its reachable
verified dictionary snapshot. The initial target is Windows x64 with Int, Bool
and Unit values, Int/Bool-backed nominal scalar values, and acyclic records of
these values. Unsupported types, operations and effects fail before tool
invocation; there is no interpreter fallback within a compiled program.

The host supplies frozen `NativeDiagnosticSources` from the same lowering
context, preserving the definition spans used by interpreter diagnostics.
Compile-time metadata and runtime error arguments reconstruct the structured
language error in the host wrapper. The emitted DLL returns a status and
metadata identifier; it does not unwind a managed exception across the ABI.
The legacy single-entry API retains diagnostic metadata in its compiler-side
artifact handle. Multi-entry modules also publish a native diagnostic table and
a full JSON manifest. Neither API certifies a general release package.

ABI v3 uses a 96-byte context, 8-byte value slots and a 4-byte status, with
explicit scratch/retained arena descriptors and separate call workspace.
Public output capacity is the logical root count. The historical export name
`agentlang_output_capacity()` reports required workspace slots, including record
constructor fields and helper storage. Raw callers initialize status to
InvalidRequest; early unsafe/unsupported request rejection leaves it untouched.
The managed wrapper handles initialization. The contract and independent layout
oracle are in [`abi-v3.json`](../tests/fixtures/native-conformance/abi-v3.json);
the historical v1 and v2 fixtures remain unchanged for rejection tests.
Run the optional local native gate documented in the README; compiling the
ordinary solution does not invoke LLVM tools.

Native nominal scalars retain their type key in verified/emitted values and
use the same i64 payload encoding as their base type. Frozen type metadata
reconstructs named outputs; decoding and equality do not rerun validators.
Constructors invoke the validator from the bound immutable program, including
its reachable dependencies, with interpreter-equivalent depth, fuel and failure
semantics. Unsupported validator code is rejected even in an untaken branch.
See [report 105](../reports/105-native-refined-scalars.md) for validation status.
ABI v2 adds generation/index record handles, typed graph validation and atomic
promotion of successful roots from scratch to a separate retained native owner.
`ExecuteRetained` returns a disposable owner; its first decode can occur after
scratch reuse and compiled-library disposal. `Execute` decodes and disposes it.
Physical backing includes payload bytes, node directories, descriptors and call
workspace. Shared records count once for retained storage but once per occurrence
for language value limits. Capacity errors are separate native resource outcomes.

Typed entry uses `ExecuteRetainedWithInputs` and ordered `IrEntryArgument`
values: selected retained roots and Int/Bool/Unit messages. The interpreter has
an equivalent opaque-owner API. Input and target must share the exact verified
program instance; separately constructed snapshots, even with identical schemas,
are rejected. Primitive messages cannot impersonate nominal types. Retained
values do not rerun refinement validators on entry.

The native call borrows its input owner through execution, copies its complete
immutable graph into fresh scratch, and promotes only reachable output nodes
into a new retained owner. It never decodes that graph through managed values.
Failed calls leave the old state intact. This currently copies input and output
graphs; it is not zero-copy persistent state. ABI v3 adds explicit readonly input
owner, root and type-ID fields. Cross-snapshot state migration, including hot
replacement of a dictionary holding live state, remains a separate requirement.
See [report 107](../reports/107-native-state-reentry.md) for the validation scope.

A small freestanding C runtime is embedded as source and compiled fresh with each
artifact. The F# host allocates backing buffers; generated code has no managed
callbacks, OS allocation or arbitrary .NET access. Repeated graph validation can
be quadratic, so this is conformance evidence, not a throughput claim. Records
containing Float, String, containers, effects and recursive type graphs are still
rejected before Clang. [Report 108](../reports/108-native-mailbox-suspension.md)
demonstrates source-defined handlers suspending/resuming through a .NET host
with one reusable scratch owner. Production native dispatch, real I/O, JIT and
standalone release packaging remain future work.

## Standalone module and mailbox boundary

`LlvmAot.compileModule` accepts named verified bodies from one exact authorized
program instance and returns artifact paths without loading the DLL. Module
ABI1 has deterministic entry IDs, exact signatures, a shared type universe,
workspace requirements, entry pointers and compact diagnostics. Its semantic
fingerprint covers emitted LLVM and semantic metadata; separate manifest hashes
identify generated sources and the built DLL. It excludes optimization and
output-directory paths from semantic identity. It does not promise migration
between distinct dictionary generations.

Mailbox control ABI1 is separate from execution ABI3. A Windows native controller
uses caller-supplied bounded storage for fixed mailbox identities, one scratch
arena/workspace and two retained banks per mailbox. Selected handlers have
`Int -> State`, `State Int -> State Continuation`, and
`State Continuation Int -> State` signatures. State and Continuation are distinct
supported record types; messages use canonical Int, not arbitrary refined Int
storage. These are explicit handler boundaries, not automatic async lowering.

Successful turns replace retained state atomically; failure leaves prior state
and the pending completion available for retry. Actual OS thread identity and
an in-handler guard protect shared scratch. Checked runtime/mailbox/sequence
completion identities reject stale delivery. Fresh scratch and staging owner
generations reject stale handles after reuse; exhaustion fails without wrapping.
Two generations per invocation impose a finite lifetime under ABI3's 32-bit
owner generations. This must be revisited before a long-running production target.

Caller storage and loaded module lifetimes remain explicit obligations of the
trusted C host. Disposal clears live state but does not free caller storage.
All backing, including inactive banks and metadata, remains reserved until the
caller releases it. Native execution of this pure subset requires no managed
turn callbacks; frontend/compiler tooling remains F#. JIT, language-level async,
real I/O buffers/cancellation, a genuine whole-request comparison and general
release certification remain future work.
