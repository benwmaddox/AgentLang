# Native nominal scalar implementation plan

2026-10-10. Next secondary backend slice after report 169 is published. This
does not replace the requirement for validated refinements or claim native
support for Email. The interpreter's verified semantic IR remains authoritative.

## Delivery order

First preserve unvalidated nominal Int identity, such as OrderId or Meters,
including supported record and Option/Result nesting. Then implement a bounded
predicate-bearing Int refinement (PositiveId), followed by nominal String and
its predicate support. A nominal name alone is not a value-validation rule.
Unsupported refinements must continue to fail explicitly rather than executing
as their base type with validation omitted.

## First slice: nominal Int

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
- String/Bool/Float wrappers and predicate-bearing scalars remain explicit
  unsupported errors, including inactive sum alternatives.
- Lifetime escapes and short-capacity failures preserve outputs and cleanup.
- Fresh native value-stack and applicable mailbox regression gates, plus the
  full local Release gate, pass before publication.

## Next slice: predicate refinement

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

## Investigation references

Read-only planning inspected `TypedIR.fs` scalar definitions and generated-target
verification, `IrInterpreter.fs` wrap/unwrap execution, `LlvmAot.fs` existing
scalar/validator lowering, `OwningStackAot.fs` program inspection, block validation,
host codecs and selected dynamic descriptor emitter, and `ArenaLifetime.fs`
conservative transfer handling. Existing LLVM tests cover nominal identities,
PositiveId and frozen-validator replacement. No code or executable validation
changed during this planning pass.
