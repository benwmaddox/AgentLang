# Native nominal scalar implementation plan

2026-10-10. The first nominal Int slice is complete and validated in
[report 170](../reports/170-native-nominal-int.md): 521 LLVM assertions, 94
independent nominal cases across O0/O2, mailbox regressions and all 37 full local
Release checks pass. Predicate-bearing Int also passes its independent native,
mailbox-regression and full local gates in report 172. The bounded validated
String scope below is tracked by report 174; general Email validation remains
subsequent work. The verified semantic IR remains authoritative.

[Report 172](../reports/172-native-refined-int.md) completes ordinary owning
predicate-bearing Int: 595 LLVM assertions, 44 independent native checks, 1,587
mailbox regressions and all 37 full local Release checks pass. Ordinary raw host
inputs run the frozen predicate over active nested values before executing the
body. Refined mailbox layouts remain rejected until their own external ingress
is validated. Nominal identity alone is not sufficient at either boundary.

## Delivery order

First preserve unvalidated nominal Int identity, such as OrderId or Meters,
including supported record and Option/Result nesting. Then implement a bounded
predicate-bearing Int refinement (PositiveId), followed by nominal String and
its predicate support. A nominal name alone is not a value-validation rule.
Unsupported refinements must continue to fail explicitly rather than executing
as their base type with validation omitted.

## First slice: nominal Int (completed in report 170)

`IrScalarTypeDefinition` already stores a base type and optional frozen validator;
IR verification checks exact generated wrap/unwrap targets and nominal identity.
The interpreter represents a scalar as its nominal key plus base value. Keep
those semantics in the selected owning descriptor backend, not the historical
fixed emitter or a source-AST execution path.

In `OwningStackAot.fs`, permit only scalar definitions with Int base and no
validator. Keep a distinct IrNominal key, source name and TypeId, with eight-byte
payload and extent. Extend block validation, stack planning, descriptor emission,
and host value measurement/encoding/decoding for generated wrap/unwrap. Wrapping
and unwrapping retag the descriptor without copying payload, preserving owner
provenance. The physical descriptor kind can remain Int; its separate TypeId
retains nominal identity. An ABI change is unnecessary unless implementation
evidence contradicts this representation.

Host input must be `NamedValue` with the exact expected name and Int payload;
reject a bare Int or another wrapper even when its bytes are identical. Host
decoding must reconstruct that name. Conservatively retain existing lifetime
origins until transfer modeling proves a rewind safe. No opportunistic rewind,
compaction, reference-counting mechanism or new source ownership syntax belongs
in this slice.

## Independent acceptance

- Interpreter and owning O0/O2 execute the same verified program/body, retaining
  nominal host inputs and outputs.
- Two distinct Int wrappers with identical bytes remain distinct types. Wrong
  accessors, bare-Int input and wrong nominal input reject atomically.
- Independent eight-byte fixtures cover zero, negative values and Int64 limits,
  with explicit distinct nominal TypeIds.
- Wrap/unwrap, ordinary calls, locals and matches introduce zero deep-copy and
  move bytes; explicit record/sum construction keeps its existing accounting.
- Records, Option and Result retain wrapped payload identity through construction,
  extraction, equality, inactive alternatives and host round trips.
- String/Bool/Float wrappers remain explicit unsupported errors, including
  inactive sum alternatives. Predicate-bearing Int is covered separately by
  report 172; unsupported validator forms still reject explicitly.
- Lifetime escapes and short-capacity failures preserve outputs and cleanup.
- Fresh native value-stack and applicable mailbox regression gates, plus the
  full local Release gate, pass before publication.

## Predicate-bearing Int (completed in report 172)

PositiveId must invoke the validator target frozen into the same verified IR.
Discover validator-only dependencies as well as ordinary calls; use ordinary
depth/fuel limits. Preserve the distinction between a false predicate
(`REFINEMENT_FAILED`) and a validator execution error. Validate construction,
external entry boundaries and output publication wherever the language contract
requires it; physical byte scanning does not enforce a predicate. Workspace
lifetimes and failure rollback need independent tests. Do not resolve predicates
from mutable compiler state or silently erase validation.

Record validators, Float/List wrappers and general Email predicates are later
work. This delivery order establishes partial native support while retaining
the full strong-type requirement; it does not redefine that requirement as
nominal naming alone.

## Validated String (report 174 scope)

Extend ordinary owning-native compilation to a String-backed refinement such as
NonEmptyString, with a pure frozen String -> Bool validator. Reuse the existing
dynamic UTF-16 String layout and descriptor kind; preserve distinct nominal
TypeIds, exact host identities and owner ranges. Construction and raw external
entry must validate active nested record, Option and Result payloads before the
body or output publication. Wrap/unwrap must retag without payload copying.

Independent O0/O2 acceptance must pin bytes, layout, recursive admission,
frozen validator identity, atomic failures and ownership accounting. Layout
schema 3, stack ABI 1 and module ABI 1 remain unchanged. Refined mailbox layouts,
unvalidated String wrappers and Bool/Float refinements remain unsupported.
This slice does not establish general Email validation or add native I/O. Run
fresh LLVM, native value-stack, mailbox and full local gates before reporting
completion. [Report 174](../reports/174-native-refined-string.md) records gate
results and retained failed attempts; its status is the completion record.

## Investigation references

Read-only planning inspected `TypedIR.fs` scalar definitions and generated-target
verification, `IrInterpreter.fs` wrap/unwrap execution, `LlvmAot.fs` existing
scalar/validator lowering, `OwningStackAot.fs` program inspection, block validation,
host codecs and selected dynamic descriptor emitter, and `ArenaLifetime.fs`
conservative transfer handling. Existing LLVM tests cover nominal identities,
PositiveId and frozen-validator replacement. The initial planning pass changed
no executable code. Report 170 records the subsequent first-slice implementation,
failed attempts, accepted validation and remaining boundaries.
