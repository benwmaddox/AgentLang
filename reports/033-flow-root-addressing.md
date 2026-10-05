# Flow absolute-root addressing

Status: implementation and focused acceptance tests are in place; Flow and lint execution are pending the coordinating agent's serial test slot. No durable Flow source or default frontend cutover is claimed.

## Contract

The opt-in Flow frontend now supports `::identity(value)` as an exact lookup of the one-segment dictionary key `identity`. `identity(value)` retains ordinary short-name matching, and `ns::identity(value)` continues to address `ns.identity`. Root qualification is not encoded as a pseudo-namespace and is not erased before resolution. Static list callbacks support the same exact form, for example `items.map(::identity)`.

Root targets keep a target span covering `::` and the key, plus a full call span that covers the arguments. A root target must be one valid identifier. Callback references use a closed qualification union and validate that the name shape agrees with `ExplicitShort`, `NamespaceQualified`, or `AbsoluteRoot`. These checks also run for host-built ASTs before public rendering and lowering.

The root resolver consults only the exact dictionary key; it does not try suffix candidates or generated aliases. Static callback resolution keeps the exact identity and then applies the existing input/output/effect checks. Argument binding, written evaluation order, scalar-output boundaries, IR lowering, source mapping, and dot receiver evaluation continue through the existing Flow and Core paths.

## Focused acceptance added

Flow tests cover root call and callback parser/render round-trips, target/full spans, malformed root namespace syntax, and invalid host-built root/callback ASTs. Lowering tests create colliding `identity` and `one.identity` words with different output types and bodies, then check exact root and namespace IR target identities while ordinary and explicit-short forms remain ambiguous. Additional cases check no same-suffix fallback, exact callback signature and output-arity validation, effectful callback target identity, root named-argument evaluation order, root-call receiver chaining, and root-call test/example attachment rendering and execution.

Flow lint tests verify that a local read inside a named root-call argument counts as a use at the argument's source location. Existing generated constructor names and nominal type behavior retain their prior paths.

## Validation

The first early Core Release build caught two implementation issues: a malformed list expression in the callback diagnostic and a root-call selector outside the mutual-recursion group. Both were corrected. The coordinated Core Release rebuild then passed with zero warnings and zero errors. The initial errors are retained in report 034's evidence; the Core source has remained frozen since the successful rebuild.

The first focused Flow test build failed on a helper that treated the inspected `IrExecutableBody` as `VerifiedIrBody`, plus two warnings-as-errors from discarded parser diagnostics. The helper and assertions were corrected; the raw failure is retained in [root's integration evidence](evidence/034-focused-flow-build-failure.json). The first focused execution then passed Flow with 406 assertions and lint with 68 assertions, both exit code 0; root saved those runs in [Flow evidence](evidence/034-focused-flow-initial-pass.json) and [lint evidence](evidence/034-focused-lint-pass.json).

Independent review requested one additional exact-root effect-boundary case: an effectful root callback must be denied before instructions/providers even when its list is empty, despite a pure same-suffix namespace callback. The final Flow suite passes with 413 assertions, exit code 0; lint passes with 68 assertions. See the [final Flow output](evidence/034-focused-flow-final-pass.json), [initial Flow pass](evidence/034-focused-flow-initial-pass.json), and [lint output](evidence/034-focused-lint-pass.json). The independent final review found no material findings. The full Release gate is in progress under root ownership, so no full-gate or exact-commit CI result is claimed here.

## Limits

This is a Core/frontend naming slice. Runtime protocol, storage schema, durable source publication, batch lowering, default frontend selection, and benchmark harness behavior are unchanged. It does not establish agent productivity, context savings, or memory improvements. The future signature-only batch-resolution boundary is recorded in [the project-lowering contract](../docs/FLOW-PROJECT-LOWERING.md).
