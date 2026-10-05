# Milestone 006: verified source-to-IR lowering

Status: lowering checkpoint validated; interpreter cutover remains in progress.

The compiler now lowers current source expressions and generated record/scalar operations into verified typed semantic IR. Detached eval, test and example bodies bind to the exact immutable program snapshot. Runtime still executes the checked AST, and the `ir` command still shows that checked tree. This report does not claim authoritative IR execution, interpreter parity, LLVM support, or experimental performance gains.

## Delivered behavior

The existing inference walk now emits typed annotations used by lowering, preserving one stack/effect checking implementation. Lowering resolves stable word IDs and revisions, snapshot-scoped nominal keys, concrete primitive and callback signatures, lexical local slots, structured branch/case joins, source sites and own-body coverage obligations. All currently supported expression forms have explicit IR mappings.

Executable compiler handles retain the fixed trusted primitive registry derived from `Compiler.primitives`, keyed by actual `BuiltinOp` identity. Primitive aliases cannot forge signatures or effects. Public verifier-only model handles are not backend-authorized. The backend registry check rejects untrusted handles or a catalog mismatch before execution; the future interpreter must invoke this check.

Detached bodies retain the same verified program object and require an exact compiler-context fingerprint. Coordinating review found that the initial `%A` fingerprint truncated long structures: `[1 .. 300]` and a version with a changed final item formatted identically. Exhaustive framed serialization replaced debug formatting. Regressions now reject stale bodies after late AST edits at the same ID/revision, fields beyond the hundredth record field, and deeply nested signature edits. Nonfinite Float and null String constants are also rejected at the executable IR boundary.

Coverage obligations remain requirements, not evidence that tests exercised them. Library gates still depend on measured own-body execution traces.

## Validation and evidence

`./scripts/Validate.ps1 -Configuration Release -ReportPath .agentlang/reports/006-lowering-validation.json` passed on the working tree based on `f89df3b96a60f610d1039e94a61e781917e61180`. Evidence records `dirty: true`; this is not clean committed-revision evidence.

- Fresh Release build: zero warnings/errors.
- Language: 27 groups, 465 assertions; IR: 10 groups, 67 assertions.
- Harness: 98; business: 113; source: 57; storage: 64; conventional tools: 63; discovery: 53 assertions.
- Vocabulary analysis: 32 assertions, described in [milestone 007](007-vocabulary-analysis-foundation.md).
- Fresh-process persistence projection: 99 checks; working and staged whitespace checks passed.

Machine-readable evidence is saved as [validation](evidence/006-working-tree-validation.json) and [persistence projection](evidence/006-working-tree-projection.json).

An independent [parity verifier](../docs/IR-PARITY.md) now runs bounded, isolated JSONL processes with fixed expected oracles and field-specific cross-runtime comparisons. It retains requests/responses, artifact hashes, revisions and diagnostic fields. Its pinned-baseline self-comparison passed 314 checks across six fixtures, 12 sessions and 51 requests per side. [Saved evidence](evidence/006-parity-selfcheck.json) labels this infrastructure-only; identical binaries do not prove AST-versus-IR parity.

## Feedback and remaining gate

The next gate is a separate interpreter consuming only verified IR. Engine retains storage, capabilities, virtual providers and task policy. The cutover must preserve nominal identity, diagnostic source origins, capability preflight even for empty effectful callbacks, measured coverage, collection iteration charging, 10,000-step/64-depth limits, exact durable candidate projections, reload and rollback. Examples remain compile-checked metadata.

Only after every runtime path uses IR without an AST fallback, all existing acceptance checks pass, and the candidate is compared against the pinned AST runtime will semantic IR be called authoritative. [The migration plan](../docs/IR-MIGRATION.md) retains these criteria. LLVM JIT/AOT remains conditional later work.

Independent read-only review found no material defect in the scoped lowerer/verifier. The executable backend must dispatch PrimitiveId through a fixed host implementation mapping and pass its own canonical catalog; matching a contract alone does not identify a host implementation. This is a trusted-host boundary, not language access to arbitrary F# metadata.

The new lowerer build was also compared against the pinned published AST CLI with the same verifier: 314 checks passed. [Saved compatibility evidence](evidence/006-ast-compatibility.json) establishes selected cross-build AST behavior only; both builds still execute AST, so it is not interpreter-on-IR parity.
