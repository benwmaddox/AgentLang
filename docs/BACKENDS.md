# Shared semantic IR and later native backends

This is the architecture contract. Source lowering, IR verification, a standalone interpreter, and structured inspection are implemented. The public Runtime now executes verified IR through the interpreter; cutover validation is recorded in report 010. LLVM backends do not exist yet.

Source → parsed AST → resolved/type-and-effect-checked definitions → typed semantic IR → interpreter.

The IR records exact input/output stack types, local types, resolved stable word identities, declared/transitive effects, constants, structured control flow, and source spans. Record construction/field access, nominal scalar validation, closed containers, exhaustive cases, and static collection callbacks must have explicit operations or explicit checked calls. Keep it small and printable. Metadata and authoring source remain available separately.

The first lowering milestone preserves existing semantics and protocol behavior. Run all current acceptance tests against the IR interpreter, plus focused tests for stack joins, locals, error spans, nominal identities, callback effects, and coverage mapping. Branch/outcome coverage must continue to identify source-level obligations after lowering. Runtime errors remain structured; host exceptions cannot become successful error expectations.

Later, conditional on successful language experiments:

| Environment | Execution and shipped components |
| --- | --- |
| Development | Interpreter, REPL, introspection, hot replacement, compiler; LLVM JIT for selected stable/frequent words |
| Release | LLVM AOT native code and necessary minimal runtime; no interpreter or compiler |

Define conformance fixtures before adding native backends. They must agree on Int overflow, finite Float policy, nominal validation, errors, effect authorization, container limits, evaluation order, and observable results. Native optimization cannot remove or reorder observable effects contrary to those semantics.

Development calls resolve through versioned word identities. Hot replacement must invalidate compiled callers or route them through an updated dispatch boundary; stale compiled bodies must not silently retain replaced behavior. Pin the release dependency graph and reject unresolved candidates/temporary words before AOT.

LLVM integration, ABI/layout, allocation strategy, linking, debug information, and native performance targets remain later design work. Require explicit versions and deterministic layout oracles when those contracts are introduced. Do not make LLVM a dependency of the initial runtime or agent pilot.

Later syntax research may introduce a frontend with named inputs, expression notation, or pipelines while retaining local flow through recently produced values. All frontends must lower to this same typed semantic IR and preserve evaluation order, diagnostics, effects, and source-level coverage obligations. Source notation and backend memory behavior are separate experiments. LLVM is an execution/code-generation backend, not necessarily a replacement for the F# host/compiler implementation; lower memory usage requires measured allocation and value-layout choices. See [the late syntax research item](PRD.md#late-research-syntax-and-stack-locality).
