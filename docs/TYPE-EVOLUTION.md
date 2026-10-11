# Atomic type evolution

Status: record-field evolution implementation, 2026-10-10. Validation and publication
status are recorded in [report 188](../reports/188-atomic-record-evolution.md).
Durable type-source history and inspection are validated in
[report 187](../reports/187-durable-type-source-history.md).
The interrupted [typed-reference trial](../reports/185-typed-reference-maintenance-results.md)
demonstrated this missing prerequisite. This document defines the runtime contract
and the separate persisted-seed readiness control.

## First supported operation

Start with changing fields of existing Flow-authored records in an explicit task.
Keep record names, function identities and library maturity. Allow new nominal
types to be introduced in the same proposed project. Do not initially permit
changing an existing scalar's representation or validator, an enum's cases, or
converting one kind of type into another. Existing validator freezing remains.
Field evolution also preserves each existing record's validator declaration;
it cannot add, remove or replace that validation rule.

Use the existing `define` project request rather than add a second authoring API.
Add an exact `expectedTypeSources` map from every replaced record name to its
current authored source hash; retain `expectedRevisions` for replaced functions.
Use the hashes exposed through type inspection. A source hash is a concurrency
token, not a semantic identity. Reject stale, missing, extra or duplicate owners
before staging. New definitions must have unused names and are distinguished
from replacements.
Type-CAS requests must admit zero or one changed function as well as multiple
functions; the current word-only two-function minimum cannot govern this route.
Word-only requests retain their existing validation rules.

## One proposed execution environment

Build a detached DictionaryState containing all proposed declarations and
attachments. Preserve omitted tests and examples; an incompatible inherited
case must cause rejection unless the request explicitly supplies its adaptation.
In the first slice, adapt standalone inline cases with their owner's CAS token.
Retain existing test-file wrappers unchanged and recompile their overrides and
cases. Reject requests that edit wrappers in this transaction; reject a schema
change if an unchanged wrapper becomes incompatible. Atomic wrapper adaptations
can follow once their shared-scope ownership protocol is designed.
Inline Flow case adaptation requires an authored function owner and its revision
token. Generated constructors/accessors have no such authored owner. Retain and
recompile their existing Stack cases unchanged; an incompatible generated case
rejects the proposal. This stage does not add a generated-case ownership API or
silently remove legacy evidence. Field changes can use authored consumer cases
whose owners participate in the transaction.
Resolve generated constructors/accessors from the proposed record fields.
Recompile every authored function, test, example and test-file override against
the same snapshot. Recheck exhaustive matches, effects, validator dependencies
and library dependency closure. Never update the type map underneath old IR.

Reuse `compileRuntimeSnapshot` and the immutable executable snapshot boundary.
The current compiler fingerprints its context, including record field names and
types, scalar base types/validators and enum cases (`Compiler.fs`,
`snapshotFingerprint`); retain that check rather than introduce a second nominal
identity system. Test that changed schemas produce different fingerprints,
and that detached bodies and native layouts cannot
be attached to a different program. Eval results currently leave their execution
as serialized values; there is no persistent REPL value stack to migrate.
Retained mailbox state or native activations must remain attached to their old
program, or cause update rejection. Do not reinterpret live values by type name.
Changing stored application data is a separate explicit migration, outside this
first operation.

## Staging and publication

Add record/source backups alongside existing word replacement backups. Durable
projection must restore the old schema while any part of the change is staged.
The selected type and function closure must publish as one group; unrelated
candidate commits cannot accidentally publish half a schema change. Discard and
task abort restore the exact original dictionary, executable and manifest.

Publication runs the affected owners' attached tests and transitive callers'
tests using existing effect injection. Changed library functions and affected
library callers must satisfy existing branch, instruction and finite-value
coverage gates. Require tests for changed generated constructor/accessor
consumers, including owners mentioned only by metadata or type signatures.
Passing compilation alone does not establish that a schema change preserves
behavior.

Use the existing storage transaction to write sources, bindings and manifest
before activating the final snapshot. Retain previous type source references
in committed history so a record revision has inspectable provenance. Add
`history` with a type selector to inspect those retained sources; current
word-only history is insufficient. Preserve its word-selector behavior.
Audit manifest format, fixture oracles, reload verification and source projections
together if adding history changes the persisted contract. A failed write,
test, coverage gate or stale CAS leaves the old manifest authoritative.

## Acceptance and validation

The first positive example replaces a String field with a distinct nominal
String type, adapts its constructor consumers and tests, publishes, and works
after a fresh process reload. Raw String and another nominal String type must
be rejected for that field. Preserve inherited assertions after permitted typed
adaptation; verify exact function IDs and retained library qualification.
Assert unchanged public entry signatures separately from explicitly permitted
helper and constructor signature changes.

Negative cases cover stale CAS, incomplete caller changes, unadapted inherited
tests, changed validator closure, failed tests, missing library coverage,
unrelated publication while staging, discard, task abort and failed persistence.
Audit generated accessors, callback bindings, test-file replacements, historical
source references and old executable/native artifact attachment. Run focused
Flow runtime and persistence tests, IR/native snapshot checks where affected,
then the complete local validation suite before publication. No new external
effect provider or arbitrary host invocation is needed.

Only after this positive migration works through the public protocol should the
maintenance comparison restart with fresh participants and newly frozen runtime
inputs. Keep the interrupted trial separate from subsequent outcomes.
The readiness control must start from the same persisted, unmigrated seed as a
participant and perform the migration through its exact allowed protocol. A
fresh project built directly with the desired schema does not validate this
prerequisite. Retain the migration trace, publication and fresh-reload receipts.
Include `task.abort` in the new participant allowlist if rollback is part of its
task; the interrupted retained participant's frozen allowlist omitted it.

## Implementation sequence

Legacy manifest format 5 retains only current type source heads. The format-6
type-history prerequisite is now implemented, including explicit transition,
parser/serializer validation, fresh-reload checks and supported older-format
loading. Existing word history remains compatible; older manifests are not
silently reinterpreted as a new history format.

The runtime staging transaction uses record/source backups alongside word
replacement backups and a group publication boundary: durable projection and discard
restore the old schema, while committing any group member includes the complete
schema/function change. Compilation uses one detached proposed snapshot before
activation. Field addition, removal, renaming, type changes and ordering are all
part of this contract; a type-only-field slice cannot establish its acceptance.

Finally, validate migration from the persisted unmigrated trial seed through the
participant's permitted protocol, including original test preservation, entry
signatures, library qualification, publication and a fresh reload. Only this
readiness control permits a new frozen participant cohort. Runtime acceptance
does not establish that readiness control or an agent-efficacy result. The
type-history prerequisite is complete.

For the storage implementation, keep current `Types` heads and add a required
format-6 collection of prior type-source rows. Each row retains the type name,
positive revision ordinal and exact source reference/format/validator metadata.
Prior ordinals must be contiguous and belong to a current head; the head's
ordinal follows the retained rows. Include every historical reference in
hash-verified load/commit closure. Missing format-6 history metadata is an error,
not an empty-history default. Older manifests have an implicit initial source;
record it when that source is first displaced, without inventing actor or time
metadata. Preserve version-5 attachment-format requirements at version 6.

`history` with a type selector reads these manifest references as source data;
it must never compile a historical schema into the active environment. Reject
ambiguous type-and-word selectors, preserve word history response shapes, and
keep source text queries compatible. Provide an explicit type inspection payload
for the current source hash rather than changing raw `source(type)` text.

The restart control must migrate the retained seed's `Scan.reference`,
`ScanInput.reference` and `ShipmentScanLookup.target`, plus the permitted
`shipment.find-scan` helper parameter, to `TrackingReference`. Preserve helper
identity/library maturity and both ingest entry signatures. Keep the original
18-case behavior oracle and add separate publication, inherited-evidence,
type-history and fresh-process reload checks; no single oracle proves all of
these. Do not rerun preparation over frozen study-184 inputs or pool new
participants with the interrupted result.
