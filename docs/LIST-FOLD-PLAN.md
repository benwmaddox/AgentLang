# Typed static list fold

Status: implementation plan, 2026-10-06. Parent `f55057b`; not an implemented
business fixture or an allocation-policy change.

## Contract

Flow authoring: `items.fold(seed, word step)` or a qualified static reference
such as `items.fold(seed, domain::step)`. Seed is an ordinary positional
expression; callback is the final, statically resolved reference. Existing
ordinary dot calls with value arguments remain ordinary calls. No runtime
function values, captured locals, named callback argument or early-exit form.

Core/Stack: `list.fold step` consumes `List<Item> Accumulator` and returns one
`Accumulator`, preserving any earlier stack prefix. The callback specializes
to exactly `Accumulator Item -> Accumulator`; resolve existing polymorphic
primitives to closed types, without adding user-defined generics or coercions.
Nominal item and accumulator identities must remain distinct from representations.

Evaluate receiver then seed left to right. Visit items left to right with
`[accumulator; item]`; replace the accumulator with the callback's single result.
An empty list returns its seed. A Result accumulator may carry an error through
later items; fold does not short-circuit.

## Semantic and quality boundaries

Add `Expr.FoldList of string * SourceSpan` and authoritative
`IrOperation.ListFold of IrResolvedCall * IrType * IrType`, with the types ordered
item then accumulator. Verify concrete stack/callback signatures and effects;
include callback identity in dependency/caller/cycle and vocabulary analysis.
Preflight declared callback effects even for empty input. Distinguish preflight
from actual callback execution when instrumenting word use.

Fold owns empty/nonempty branch outcomes for library coverage. Callback body
branches belong to the callback's own tests; caller tests cannot certify its
library coverage. Preserve current execution fuel, call-depth, collection and
structured-value bounds, error propagation and source origins. No unverified
placeholder or alternative AST execution path.

Flow callback binding occupies argument index 1. Preserve structural paths,
resolved identities, exact source, rename/replace and reload behavior. Stored
callback stage is `fold`, syntax descriptor is `list.fold`, printed IR kind is
`list-fold`, consistent with the existing layer-specific naming conventions.

IR JSON format advances from 2 to 3 because a new operation kind is serialized.
Audit formatting assertions, parity fixtures and current oracles. Storage v3's
callback binding shape is unchanged: extend the validated stage set and retain
unknown-stage rejection. Flow/1 is additive; old valid sources must remain
unchanged. Older runtimes may reject the newly authored form; do not rewrite
historical evidence or silently claim forward compatibility.

## Ownership and acceptance

Backend owns Core/Stack/compiler/IR/interpreter/source/storage/runtime/vocabulary
modules. Frontend owns Flow syntax/parser/lowering/rewrite/persistence/lint.
Separate test ownership covers IR/source/format/vocabulary/parity and
Flow/runtime/storage/general acceptance. Workers share the canonical checkout,
preserve others' edits, and do not build or perform Git operations. Root
serializes validation after source handoff.

Acceptance includes noncommutative order, empty seed identity, stack prefix,
nominal record/scalar accumulators and items, closed primitive specialization,
wrong callback arity/order/input/output, no capture, dependency/effect closure,
denial on empty without provider invocation, fuel/value bounds, exhaustive IR
verification, library empty/nonempty gates, exact source/binding round-trip,
stable callback identity through reload and semantic maintenance, unknown stored
stage rejection, and default Flow plus explicit Stack conformance.

Build `AgentLang.sln --configuration Release`; run the affected Flow, IR,
IR.Interpreter, IR.Formatting, Source, Vocabulary, Storage, Flow.Runtime and
general acceptance projects with that build. Run the IR parity verifier using
fresh binaries and current fixtures. Finish with `scripts/Validate.ps1`, exact
clean committed-source CI, updated report, and private fast-forward publication.

The next business fixture still needs the documented value-policy helpers and
40–60 authored words/50–100 tests. Fold is a prerequisite, not a substitute for
that domain or controlled agent evaluation. Managed F# lists remain managed;
this adds no arena, mailbox, native allocator, JIT or AOT implementation.
