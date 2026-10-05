# Typed semantic IR migration plan

Status: in progress. The typed IR model and verifier exist; AST lowering and
the IR interpreter are still pending. The first backend is an interpreter over
the typed IR. LLVM remains conditional follow-on work and is not a dependency
of this migration.

## Current gap and boundary

The language currently parses to `Expr`, infers types and effects in
`Compiler.inferBody`, then interprets the same AST in `Runtime.runBody`. The
`ir` command currently says it displays a checked expression tree that is
interpreted directly; it is not executable IR. This migration is complete only
when REPL evaluation, words, callbacks, tests, candidate validation, and reload
all execute compiled IR and there is no runtime AST fallback.

## Implementation status

`TypedIR.fs` now defines closed executable types, snapshot-scoped nominal keys,
stable linked word targets, per-call concrete primitive instantiations, typed
stack/local block shapes, explicit structured cases, generated record/scalar
operations, source maps, and coverage obligations. IR effects use a closed
ten-case `IrEffect` union; `IrEffects` maps validated source effect names at
the planned compiler boundary and formats them deterministically. The source
name conversion helper exists, but no AST lowering uses it yet.
`IrVerifier.verify` checks call identity/revision/signatures, primitive
specializations, effect consistency, container and generated operation types,
callback signatures and effects, branch joins, case-local scope, source
ownership, exact coverage categories, and acyclic user/generated call graphs
before returning a verified program handle.

`tests/AgentLang.IR.Tests` exercises the verifier with constructed executable
snapshots. This establishes the data model and verifier contract only. The
existing compiler does not lower `Expr` to this IR, and `Runtime` still
executes the source AST. The current `ir` command is not yet a rendering of
verified executable IR. No claim of IR execution, interpreter parity, or
backend cutover is made until later migration stages pass their acceptance
criteria.

Keep `Expr` as the source AST used by parsing, canonical source rendering,
diagnostics, and semantic edits. Add one closed, typed, resolved representation
between compilation and execution. Preserve current language behavior during
this migration: evaluation order, stack effects, nominal checks, effect policy,
container limits, diagnostics, and coverage obligations do not change.

## IR contract

The smallest complete form is a typed structured stack IR. It retains the
language's existing stack model and represents branch bodies as nested blocks;
each block declares its entry and exit stack and local shapes. `if`, Option
match, and Result match have a verified join shape. An eventual LLVM lowering
can turn these joins into basic blocks and phi values without making LLVM's
representation the source of language semantics.

Executable types are closed: built-in types, recursively parameterized
List/Option/Result, and nominal types identified by opaque `ProgramTypeKey`
values. The compiler resolves source type names once against an immutable type
table owned by a compiled program snapshot. IR and runtime values carry those
keys; execution never searches the table by a source name. No `TVar`, unresolved
source name, parser token, or documentation object appears in verified
executable IR. Primitive-polymorphic calls such as `equals` are instantiated
with concrete types at each call site.

Each call stores a `ResolvedTarget`: stable `WordId`, linked revision, concrete
input and output stack types, direct/transitive effects, and a display name for
debugging. The name is never used to resolve the call during execution. Builtin
IDs come from a fixed primitive catalog. User IDs use the existing persisted
word identity. Generated record/scalar operations keep their current
deterministic IDs derived from generated word names. Current type and field
names are immutable, so the compiler can resolve these IDs and `ProgramTypeKey`
values against each complete snapshot without changing Storage format v1. A
snapshot contains the immutable nominal type table needed by its IR. This is
not a durable identity contract for future rename operations: before adding
type or field rename, introduce persisted `TypeId` and `MemberId` values and a
versioned storage migration that preserves them. That migration is a separate
follow-on, not a prerequisite for this interpreter cutover.

Every function records its signature, declared effects, inferred transitive
effects, and verified body. The verifier rejects unresolved IDs, signature or
effect disagreement, free type variables, malformed constructor payloads,
invalid block joins, missing case blocks, and incompatible callback targets.
An IR program is immutable for an execution snapshot. Staging, replacement, or
reload creates and verifies a complete new snapshot. Resolved references carry
the target revision; callers are relinked or invalidated before the new snapshot
is visible, so no body silently holds a stale replacement.

## Source construct lowering

Every current `Expr` and generated word has an explicit mapping. No case may be
left as an AST node interpreted specially by Runtime.

| Source form | Typed IR form and required information |
| --- | --- |
| `Push` | `Constant` with a typed literal and result stack type. |
| `Call` | `CallResolved` with stable target ID/revision, concrete call signature, effect set, and source-site ID. Primitive and user calls share this resolved contract; primitive execution dispatches by a closed primitive ID. |
| `ConstructContainer` | Typed List/Option/Result constructor op with explicit closed type arguments and checked payload input, if present. Empty, `none`, and inactive Result alternatives retain all type arguments. |
| `MapList`, `FilterList`, `EachList` | Static list op with resolved callback ID, concrete callback signature (`T -> U`, `T -> Bool`, or `T -> Unit`), callback effects, and exact result type: map returns `List<U>`, filter returns `List<T>`, and each returns `Unit`. Callback lookup and effect checks happen before iteration, including for empty lists. |
| `Let`, `Load` | Typed local-slot store/load. Local layout is part of each block shape; rebinding follows current `Map.add` behavior. |
| `If` | Conditional structured blocks. Consume the Bool, type-check both blocks, and require equal output stack and local shapes. The join selects the values produced by the executed arm. |
| `MatchOption` | Exhaustive `Some`/`None` switch. The Some payload is a typed case-local slot; it cannot shadow an outer local and is removed at the join. Both arm stack and outer-local shapes must agree. |
| `MatchResult` | Exhaustive `Ok`/`Error` switch. Each payload has its own typed case-local slot; neither escapes. Both arm stack and outer-local shapes must agree. |

Lower currently generated words explicitly as semantic operations, not as
special AST execution paths:

- Record constructors become `MakeRecord(ProgramTypeKey, field-order, values)` and
  accessors become `GetField(ProgramTypeKey, field-key, record)`. They preserve declared
  field order and exact nominal identity.
- Scalar constructors become `WrapScalar(ProgramTypeKey, value, validator-target?)`;
  the optional validator is a resolved, pure `BaseType -> Bool` call. A false
  result remains `REFINEMENT_FAILED`; the validator target and its effects are
  included in the resolved dependency/effect graph. Accessors become
  `UnwrapScalar(ProgramTypeKey, value)`.
- Trusted primitive operations become closed `PrimitiveCall` IDs with concrete
  stack signatures and effects. Filesystem, clock, console, and other host
  effects still pass through the host capability/provider boundary; there is
  no .NET reflection or arbitrary runtime call.

The `ir <word>` command must render the verified executable function, including
resolved IDs, concrete types/effects, structured branches, and source-site IDs.
It must not be an alias for `Source.renderBody` or `Compiler.sourceExpressions`.

## Source, errors, and coverage metadata

Keep authoring and executable data separate. `WordDefinition`, `ParsedSource`,
documentation, tests/examples, maturity, revision, and canonical source remain
the authoring/persistence model. The IR contains only execution data and stable
IDs. A sidecar source map relates each IR operation to its AST source-site ID
and original `SourceSpan`; generated operations map to the owning type/field
declaration. Rebuilding IR from persisted source must reproduce equivalent
semantics and source mappings. Do not make serialized machine code authoritative
in this phase.

Coverage obligations are keyed by the owning `WordId` and source-site ID, not
IR instruction offsets. An IR lowering that expands one AST node into several
operations still represents one source obligation. Preserve current own-body
coverage: called dependencies and callback bodies do not satisfy the caller's
obligations. Keep the exact outcome set:

- `if`: `true`, `false`;
- Option: `some`, `none`;
- Result: `ok`, `error`;
- map/each: `empty`, `nonempty`;
- filter: `empty`, `nonempty`, `keep`, `drop`.

Diagnostics remain structured and retain stable codes, word identity/display
name, and source origin. Expected-runtime-error tests pass only on an exact
language diagnostic code. Static compilation errors, normal completion,
different language errors, and host failures remain failures. Source locations
must survive lowering and calls into generated operations.

## Module ownership

Keep the current `AgentLang.Core` project boundary; do not create an LLVM
project or dependency for the first migration.

| Module/file | Responsibility after migration |
| --- | --- |
| `Core.fs` | Source AST and language metadata; retain persisted `WordId` and add source-site identity types. `Expr` remains authoring syntax, not executable code. |
| `TypedIR.fs` (new) | Closed IR types, opaque snapshot-scoped `ProgramTypeKey`, resolved call records, typed blocks/functions, immutable type table/program snapshot, source-map and coverage sidecar DTOs. No parser or execution logic. |
| `Compiler.fs` | Sole AST-to-checked-IR pipeline: name resolution, concrete polymorphic instantiation, stack/local/control-flow typing, direct and transitive effects, lowering, and IR verification. Existing checks must not be reimplemented in Runtime. |
| `IRInterpreter.fs` (new) | Executes verified IR only: constants, calls, constructors, locals, callbacks, and structured cases. It has no `Expr` match and no parser access. |
| `Runtime.fs` | Owns dictionary/task transactions, candidate and persistent execution snapshots, capability/provider bindings, primitive host dispatch, test execution, coverage aggregation, and compile/relink on stage, replacement, commit, abort, and load. Remove `runBody`'s AST execution path after cutover. |
| `Source.fs`, `Storage.fs` | Continue rendering and persisting canonical source/metadata and existing stable word IDs in Storage v1. Rebuild the nominal type table and verify IR on load; program-scoped type keys and IR caches are disposable. Do not change storage schema for this cutover. |
| `tests/AgentLang.IR.Tests` (new), Acceptance/Storage tests | Unit-test IR shape/verifier/lowering, then test Runtime behavior, persistence identity/reload, and whole-language compatibility. |

Recommended F# compile order is `Core`, `Storage`, `Parser`, `TypedIR`,
`Compiler`, `Source`, `IRInterpreter`, `Runtime`, `Protocol`, adjusted only as
needed to keep dependencies acyclic. `IRInterpreter` should receive a narrow
host interface for primitive/effect operations; it must not call arbitrary
.NET APIs.

## Migration stages and acceptance

1. **Freeze semantics and establish snapshot keys.** Add before/after fixtures for
   arithmetic boundaries, containers, refinements, all branch outcomes,
   callback effects, generated records, tests, reload, and coverage. Add
   snapshot-scoped nominal keys resolved from the immutable type table while
   retaining Storage format v1 and its existing word IDs. Existing storage
   fixtures and legacy loads must recompile successfully from source. Persistent
   type/member IDs and a schema migration remain follow-on work before rename
   support. No execution-path change yet.
2. **Compile and verify IR side by side.** Lower every row in the construct
   table, including generated operations. Add `tests/AgentLang.IR.Tests` cases
   for resolved IDs/signatures/effects, no free variables, block/local joins,
   case-local payloads, callback constraints, and source-site mappings. Make
   `ir <word>` display this verified representation. Acceptance: all current
   definitions can compile and verify with no change to their source API.
3. **Switch all execution to IR.** Route REPL `eval`, word calls, primitives,
   generated words, callbacks, tests/examples, candidate tests, and task-session
   execution through `IRInterpreter`. Compile a complete proposed snapshot
   before staging/committing it; compile again from authoritative source/IDs
   on reload. Acceptance: no runtime path accepts or executes `Expr`; malformed
   or stale linked IR is rejected before effects; candidate/temporary rollback
   and replacement invalidate/relink affected callers atomically.
4. **Prove parity and freeze the backend seam.** Run all current acceptance
   and persistence tests plus targeted conformance checks: exact structured
   errors and source origins; capability denial before effects, including an
   empty list with an effectful callback; callback and nominal type mismatch;
   both outcomes of every case; own-body coverage; stable IDs after reload and
   rename; task abort; and candidate commit with the proposed IR snapshot.
   Only after this passes should experiments decide whether a native backend
   is worth implementing.

The compiler/IR unit command added in stage 2 is
`dotnet run --project tests/AgentLang.IR.Tests/AgentLang.IR.Tests.fsproj`.
Focused regressions use the current executable projects:

```powershell
dotnet build src/AgentLang.Core/AgentLang.Core.fsproj
dotnet run --project tests/AgentLang.Acceptance/AgentLang.Acceptance.fsproj
dotnet run --project tests/AgentLang.Source.Tests/AgentLang.Source.Tests.fsproj
dotnet run --project tests/AgentLang.Storage.Tests/AgentLang.Storage.Tests.fsproj
dotnet run --project tests/AgentLang.Harness.Tests/AgentLang.Harness.Tests.fsproj
```

The final integrated gate is `pwsh -File scripts/Validate.ps1
-Configuration Release`, after adding the IR test project to the solution and
validation script. This rebuilds the solution, runs the acceptance projects,
and checks tracked/staged whitespace. The first interpreter-on-IR milestone
must pass the same gate before any LLVM work begins.

## Deferred

This migration does not add LLVM, JIT/AOT code generation, native ABI/layout,
optimization, async execution, new language syntax, or semantic changes. A
later backend must consume the same verified IR, share the conformance fixtures,
preserve effect order, errors and source maps, and use explicit development vs
release policies. It must remain gated by measured project results rather than
becoming a prerequisite for the agent pilot.
