# 188 — Atomic record evolution

Status: accepted after complete local validation, 2026-10-10.

The [interrupted maintenance trial](185-typed-reference-maintenance-results.md)
exposed a missing operation: replacing persisted record fields while preserving
function identities, library qualification and inherited behavior evidence.
[Type-source history](187-durable-type-source-history.md) is now accepted.
This stage implements the remaining runtime transaction; it is not a new
agent-efficacy result.

The contract is [Atomic type evolution](../docs/TYPE-EVOLUTION.md). A request
compares exact current type-source hashes and existing function revisions,
compiles one detached proposed environment, and stages an atomic publication
group. Record/source backups preserve the old durable schema until publication.
Commit, discard and task abort must operate on the whole group. New types and
helpers may accompany the replacement, while existing identities and library
maturity remain stable.

The field scope includes additions, removals, renames, type changes and ordering
changes. Existing scalar representation/validator changes, enum-case changes,
record validator declaration changes and type-kind conversion remain excluded.
Compatible attached tests/examples
must survive; incompatible inline cases require explicit owner adaptation.
Existing test-file wrappers are retained and recompiled, with wrapper edits
outside this transaction. Signature-only and metadata-only owners, generated
operation consumers and transitive callers must join the affected test closure.
Existing library qualification and coverage gates remain mandatory.

Inline adaptation currently requires an authored Flow function owner. Generated
constructors/accessors do not have that ownership metadata: their retained Stack
cases are recompiled unchanged, and incompatibility must reject the proposal.
This stage adds no generated-case ownership/storage API and must not discard
those cases. The complete field matrix uses authored consumer cases that have
valid revision tokens.

Acceptance requires CAS negatives, zero/one/many changed functions, new helper
and nominal type introduction, the complete field matrix, inherited evidence,
library gates, generated binding collisions, atomic group selection, unrelated publication, rollback,
failed persistence, exact type history and fresh reload. No old IR, native
activation or mailbox data may be reinterpreted using the new schema.
No host capability, external dependency or arbitrary host invocation is planned.
Security review is limited to preserving the existing capability and executable
boundaries. It does not establish that the runtime is an operating-system sandbox.
The existing real-filesystem provider checks paths before opening them; hostile
concurrent directory replacement remains a known race, requiring a protected
project tree. This transaction adds no filesystem provider or network operation.

The IR snapshot regressions isolate field names, ordering and count while
holding source text, spans and field types constant. An unchanged context must
accept its detached body; each changed layout must reject attachment to the old
verified program. Existing field-type and validator-target regressions remain.
Focused serial Release build and IR console validation passed: 21 groups,
198 assertions, zero build warnings/errors. The test-source hash is recorded in
the receipt; this proves the IR boundary cases, not runtime migration acceptance.

Focused validation used fresh serial Release builds and the Flow runtime and
storage console acceptance executables with temporary storage inside the
workspace. The complete local gate passed after review and source freeze:
37 checks, terminal exit 0, with zero solution-build warnings/errors.
The isolated business-policy preflight passed 98 checks across 30 independent
outcomes. Runtime, Storage and IR results matched the focused passing counts.
All tested source pins remained unchanged. The accepted runtime receipt also
checks CLI/Core/FSharp.Core hashes against the V2 host verification receipt and
pins the launch metadata and verified host scripts for the readiness control.

Early source review identified unfinished group-wide discard wiring and a
publication test-selection gap for generated record calls used only in attached
test/example metadata. Integration review also found separate generated-case
edits during staging could leave orphaned metadata after rollback. Source fixes
now wire whole-group restoration, include metadata-only generated dependencies
in publication checks, and reject those separate edits while the group is staged.
The bounded delta review found no further concrete implementation defect and
identified two remaining evidence gaps: validator dependency replacement, and
task abort after schema publication. Both now have passing regressions.
Initial compilation exposed implementation errors; subsequent runs also exposed
fixture syntax, expected response and generated-name collision mistakes.
Those attempts are not passing integrated validation results. After fixture
corrections, the fresh serial Release Flow build passed with zero warnings/errors,
and the complete Flow console suite passed 44 groups / 2,191 assertions.
The retained-wrapper comparison preserves all case outcomes and active overrides;
reload-specific coverage-site identifiers are excluded from that comparison.
Exact wrapper and test source preservation is checked separately.
Fresh serial Release Storage build also passed with zero warnings/errors;
the Storage console suite passed 18 groups / 572 assertions.
The focused receipt records commands, final source hashes and resolved attempt
summaries. Earlier individual failure transcripts were not separately saved.
The complete gate passed on these frozen corrected sources. CI remains manual
(`workflow_dispatch`); this acceptance was performed locally.

After acceptance, the readiness control must migrate the actual persisted,
unmigrated retained trial seed through the public protocol, preserve the original
assertions and public entries, publish, and pass a fresh-process reload plus the
independent oracle. A freshly authored already-migrated project is insufficient.
The frozen retained seed's current manifest declares format 3; its dictionary
and original task-log hashes match the recorded inputs. Earlier planning text
called it format 5. Readiness must use the actual format-3 persisted input and
verify the supported legacy load, rather than rebuild or relabel the seed.
Source preparation now includes all six inline attachment owners under word CAS,
including unchanged public entry bodies. Revision advancement is permitted;
entry IDs, signatures and library maturity are separate preservation checks.
Review found a copied source-hash error and incorrect unchanged-body drafts;
these were corrected against structured manifest reads and exact referenced
source-object comparisons. The resulting source audit is preparation evidence,
not a runtime migration or efficacy result.
An additional deterministic inherited-case audit found all 16 proposed adapted
test/example sources also changed formatting, qualified calls or branch structure,
beyond the permitted nominal-value wrapping. The source draft was corrected
from the exact baseline objects. A fresh source-only audit now passes all 16
adapted case comparisons, with the other eight inherited tests omitted from the
request and retained by the planned transaction. This audit
enumerates all 21 tests and 3 examples and reverses only nominal String wrappers
before comparison, so source rewrites cannot count as unchanged assertions.
Post-publication preservation and actual execution remain readiness requirements;
the source-only pass establishes neither.
Only then should a new participant cohort be frozen; it must remain separate
from the interrupted trial.

Evidence: [archive index](evidence/188-atomic-record-evolution/index.json) and
[tested sources and receipts](evidence/188-atomic-record-evolution/evidence.zip).
The archive includes focused and full validation, bounded independent reviews,
runtime pins, source preparation and the observed source-audit failure summary.
It does not contain raw transcripts of the earlier focused fixture failures,
or a completed persisted-seed migration or new agent trial.
