# Next bounded slice: optional complete-record validation

## Recommended slice

Add an optional pure predicate to a record definition, following the existing
scalar-validator contract. A declared predicate has the exact signature
`Record -> Bool`; the generated record constructor first assembles the complete
typed value, invokes the predicate, and only returns that value on `true`.
`false` produces one structured `RECORD_VALIDATION_FAILED` diagnostic with the
record type, constructor, and construction site. Records without a predicate
keep their existing behavior. This adds no generic proof mechanism.

Use `CustomerLookup` as the first domain case: when `found` is `some customer`,
`customer.id` must equal `target`; `none` is valid. This is a real relation
between fields and exercises `Option<Record>` access. `PopulatedEmailDeliveryPlan`
is not a useful validator example: its `first: EmailMessage` shape already
removes the empty case, and its fields have no additional invariant between
them.

Keep the source form parallel to scalar validators: one optional `validate`
word reference in the record declaration, qualified in Flow with the same
root/namespace rules as scalar validators and represented by the existing exact
Stack word reference. Do not let the predicate accept individual fields; it
must see the complete nominal record.

## Representation and enforcement

- Add `Validator: string option` to `RecordDefinition` in `Core.fs`. Parse and
  render the optional clause in `FlowParser.fs`, `FlowSyntax.fs`, and
  `Source.fs`; preserve old declarations byte-for-byte when it is absent.
- Add `ValidatorCall: IrResolvedCall option` to `IrRecordDefinitionData` in
  `TypedIR.fs`. Resolve it in `Compiler.fs`, and carry it on `IrOperation.MakeRecord`
  just as `WrapScalar` carries its validator. The IR verifier must require an
  exact `[IrNominal key] -> [IrBool]` pure call and verify it is the validator
  frozen in the nominal type table. Include it in `IrFormatting.fs` type
  fingerprints/DTOs so a changed validator cannot reuse an old snapshot.
- In `IrInterpreter.fs`, validate the assembled `RuntimeRecord` before exposing
  it. In `LlvmAot.fs`, allocate the complete record in scratch state, invoke the
  frozen validator, and publish the record output only after acceptance; false
  and malformed results must match interpreter diagnostics at O0 and O2.
- In `Runtime.fs`, mirror scalar validation for signature, purity, unknown-word
  checks, canonical source, and stable target binding. `Storage.TypeSource` and
  manifest v3 already carry a generic `ValidatorTarget`; reuse it and reject a
  missing or mismatched target on reload rather than adding a storage schema.
  Extend validator-closure freezing, candidate/commit dependency selection,
  rename handling, and `TYPE_VALIDATOR_FROZEN` checks from persistent scalars
  to persistent records. Update `Discovery.fs` and `VocabularyAnalysis.fs` so
  the generated constructor depends on and exposes its validator. Keep that
  implicit call out of authored Flow call-site bindings in `FlowLowering.fs`.

## Recursion, purity, and bypass boundaries

The validator may use this record's generated field accessors; those are how a
predicate reads the complete value. It must not call its own generated
constructor, directly or through its word dependency closure. Add the same
constructor-to-validator edge used for scalar validators to the global call
cycle check, so cross-type validator cycles are rejected too. The validator is
pure, bounded by existing fuel/call-depth limits, and introduces no effects.

The verified `MakeRecord` operation is the language-level creation path;
`IrEntryArgument` accepts primitive arguments or retained roots from the exact
verified program, not caller-supplied `RecordValue`s. Persistence stores type
source and bindings, not executable record instances; reload must restore and
verify the validator target before compiling. `ValueInspection` serializes
values but does not create executable values. Native retained roots likewise
come from the verified program. The C `al_runtime_make_record` helper is
declared in `arena_runtime.h`; it checks layout and field types but has no
business predicate. Generated LLVM must call the validator before returning
its handle. Keep this helper an internal allocator for the language guarantee.
If external C callers are intended to use the header as a supported record
constructor, that is an uncovered bypass and needs a separate ABI design because
its current type descriptor carries no validator call.

## Focused acceptance

- Parse/render/reload a validated record and an unchanged unvalidated record;
  reject unknown, wrong-signature, impure, duplicate, and stale-target
  validators.
- For `CustomerLookup`, accept `none` and matching `some`, reject mismatched
  `some` through the direct generated constructor, and assert the returned
  value preserves nominal identity. Include direct and indirect self-constructor
  recursion negatives while allowing field accessors.
- Verify the IR table, constructor operation, discovery, and persistence all
  name the same stable validator target. Replacing a persistent validator
  dependency must fail; a fresh engine must preserve both valid and invalid
  outcomes after reload.
- Run the same valid and invalid cases through the interpreter and native O0/O2,
  compare structured error code/type/source context, and exercise retained-root
  re-entry under the same verified program. Keep the C allocator layout tests;
  do not claim raw external C construction enforces the predicate.
- Add the business relation test beside `tests/AgentLang.Business.Tests` and
  persistence cases in `tests/AgentLang.Flow.Runtime.Tests`; extend
  `tests/AgentLang.IR.Tests`, `tests/AgentLang.IR.Interpreter.Tests`, and
  `tests/AgentLang.Llvm.Tests` for verifier/runtime/backend boundaries.

No business semantic decision blocks this slice. The only scope assumption is
that `al_runtime_make_record` remains an internal allocator; if it is a public
supported construction API, native descriptor/ABI work must be planned before
claiming the invariant covers that entry point.
