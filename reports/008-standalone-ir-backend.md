# Milestone 008: standalone IR interpreter and inspection

Status: standalone backend validated; public Runtime cutover and AST-versus-IR parity remain pending.

The interpreter consumes verified IR through a fixed 49-primitive dispatch table, private snapshot-scoped nominal values, and a closed primitive-payload effect interface. Program authorization and canonical registry checks occur before host hooks. The detached-body entry point accepts an empty stack; other entry signatures fail before hooks rather than executing with missing inputs. Local fuel, call-depth and collection limits are independent of host tracing.

The formatter emits explicit JSON DTOs and readable text for verified user/generated operations, closed types, linked targets/revisions, locals, source spans and coverage obligations. Primitive views are labeled generic contracts. Its nominal table contains only the requested target's recursive type references. A conservative depth guard returns `IR_FORMAT_LIMIT_EXCEEDED` before exceeding the default Protocol envelope. Coverage obligations are not measured test traces.

## Findings and repairs

Review required interpreter-local fuel enforcement and an explicit entry-stack check. Tests cover trusted handles, exact program binding, nominal conversion, denied empty effectful callbacks, local fuel and call depth.

A long flat fuel fixture exposed a verifier stack overflow before execution. The verifier now iterates over flat instructions while preserving incoming-shape checks before each source lookup. Regressions verify 10,002 operations, reject a malformed final operation, and reach `RUNTIME_STEP_LIMIT` under a no-op host. The fuel fixture keeps stack depth at most one; a separate nested-callback case proves iteration work consumes fuel. Formatter tests also caught the need for a structured depth limit rather than an unhandled JSON serialization failure.

## Validation

Fresh `./scripts/Validate.ps1 -Configuration Release` passed all 16 checks with zero build warnings/errors:

- Language: 27 groups / 465 assertions; harness: 185; business: 113.
- Source: 57; storage: 64; conventional tools: 63; discovery: 53.
- IR verifier/lowerer: 11 groups / 69 assertions; formatter: 36; interpreter: 13.
- Vocabulary analysis: 32; task-bank artifact validator: 2,114.
- Fresh-process persistence: 99 checks; working/staged whitespace checks passed.

[Validation evidence](evidence/008-working-tree-validation.json) and [persistence evidence](evidence/008-working-tree-projection.json) record base revision `3acd8e2decf95f15755ae182827cea991a04932b` with `dirty: true`. This is reviewed working-tree evidence, not clean committed CI evidence.

The public Runtime still executes AST, including tests. These standalone tests do not establish complete primitive/branch parity, integration of the formatter into `ir`, or authoritative IR execution. The next gate pairs every active dictionary with its exact verified program, migrates evaluation and durable-projection tests, preserves providers/coverage/diagnostics across commit/reload/abort, then compares against the pinned AST runtime. LLVM remains conditional later work.
