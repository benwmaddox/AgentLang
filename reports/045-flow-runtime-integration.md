# Durable Flow Runtime integration

Status: opt-in Flow integration implemented. The expanded dedicated Runtime
suite passes all 11 groups / 203 assertions, including mixed frontend reload
and v1 generated-type case compatibility. Final publication validation and
exact committed CI are being recorded below.
The full PRD remains the objective; this is not the default frontend cutover.

Implemented: explicit Flow `define`/`eval`, durable authored word/test/example
objects and history, named-parameter introspection, exact binding checks on
load/publication, identity-preserving replacements, temporary/task rollback and
snapshot restore, and the existing library passing-test/own-coverage gates.
Mixed Stack/Flow callers and generated-type cases survive fresh reload. Flow-aware
rename/deprecate and default authoring cutover remain required.

## Verified starting point

The canonical checkout started clean on `prototype` at
`9aa1fc5ef005fa2442fffb2659ccf38b2656b5d6`. The preceding manifest-v2 source
milestone is integrated into the private repository's `main` branch.

Main-branch CI run [37405398428](https://github.com/benwmaddox/AgentLang/actions/runs/37405398428)
completed successfully. Its downloaded validation artifact identifies that exact
revision, branch `main`, a clean checkout, and all 25 checks with exit code zero.
The audited [run identity](evidence/045-parent-main-ci.json) and
[validation artifact](evidence/045-parent-main-validation.json) are retained.
This proves the prior publication, not the changes planned below.

## Current gap and implementation boundary

At the starting revision, Runtime parsed the aggregate Stack export and rejected
all Flow revisions, including historical revisions. The saved implementation
now connects authored source authority to the existing verified semantic IR
and publication gates through an explicit Flow protocol selector.

The implementation plan uses an authored union keyed by stable word ID and
identity-bound Flow case sources. On load, seed the compiler context with
builtins, generated operations and current Stack heads; rehydrate every current
Flow head at its manifest revision through the source-backed project batch API.
Regenerate current call bindings and compare their complete structural keys and
stable target identities to persisted metadata. Compile only current heads:
historical revision rows do not encode a whole-project historical vocabulary.
Parse historical objects by their own source-format metadata and validate their
owners and case shape without pretending current callees reproduce history.

Review must cover candidate replacement backups,
discard, durable-state projection, source/history, rename, task abort, snapshots,
and the library word's own instruction/branch coverage. Rejection of an edit
must preserve the prior executable and authored source state together.

Flow remains explicit opt-in until its full conformance gate passes. No parser
fallback, translated RPN persistence, or regenerated bindings accepted without
comparison to persisted stable target identities are permitted.

The agreed authoring route is `define` with `frontend: "flow"`, authored `source`,
and optional `tests`/`examples` source arrays. The existing omitted frontend
continues selecting Stack. Flow expression evaluation uses `eval` with the same
explicit frontend. Unknown frontend values must return a structured error.
Replacement uses host revision checks; attachment removal requires an explicit
source-reference check. Existing publication/test/library gates remain the gate
for both frontends. Persisted binding disagreement uses
`FLOW_RUNTIME_BINDING_MISMATCH`; lowerer diagnostics retain their original codes.

A read-only `compileFlowProjectSnapshot` compiler entry point is needed for
retained Flow sources when there are no source changes. It must run the same
inventory, binding and verified-program checks without manufacturing a revision
or an attachment edit. A separate focused compiler test owner will cover this
path while Runtime acceptance owns the Engine/disk behavior.

Load-time rehydration also needs explicit host metadata. Normal compiler Add
intents assign Candidate/Project metadata; using them unchanged would lose a
stored Persistent/Library contract. A metadata-aware rehydration intent must
preserve stable ID, exact revision, lifecycle status and maturity before final
verification. It cannot manufacture a new revision or accept Primitive status
for an authored user word. Normal Add semantics remain unchanged.

## Validation and remaining scope

Record focused Runtime acceptance, fresh-process reload, focused Storage/Flow/
Source checks, and the complete Release validation gate here as they execute.
The initial fresh Core build of the snapshot helper and persistence adapter
passed with zero warnings and errors in 17.04 seconds; diagnostics are saved in
[the build evidence](evidence/045-first-core-build.json). This build precedes
Runtime integration and is not proof of durable Flow behavior. Controlled
external-agent outcomes, missing providers, complete business vocabulary,
default frontend cutover, and the remaining PRD deliverables remain outstanding.
Mailbox/static-state/arena policies remain research; no memory benefit is claimed.

The first focused Flow run compiled successfully but failed the new snapshot
equivalence fixture: it compared private synthetic marker coordinates across
rebuilds as if they were authored identity. Rebuilds allocate fresh disjoint
markers. The oracle must compare their resolved authored origins, complete
bindings, verified metadata, coverage and execution rather than raw private
coordinates. The failed run is retained in
[first focused evidence](evidence/045-first-flow-tests.json); no passing result
is claimed yet.

The second run failed a test-helper type annotation (`FS0072`); the third run
passed the normalized equivalence checks but exposed an invalid rebinding oracle.
An unchanged-source compiler rebuild receives the current context and authored
objects, not the prior durable binding rows. It can regenerate current bindings;
Runtime must compare them against persisted metadata to detect a redirected
stable target. The compiler fixture is being corrected to prove changed identity
is visible in the regenerated binding output, with durable rejection covered by
Runtime acceptance. Both failed attempts are retained in
[second](evidence/045-second-flow-tests.json) and
[third](evidence/045-third-flow-tests.json) focused evidence.

The fourth focused attempt reached the first saved Runtime snapshot/export
checkpoint and failed Core compilation: missing type annotations in a source
helper and Flow records, a sequence/map assembly mismatch, and a shadowed tuple
used as project metadata. The backup constructors compiled. These errors are
being repaired before more routes are added; see
[fourth focused evidence](evidence/045-fourth-flow-tests.json).

After the Runtime checkpoint fixes, the fifth focused run passed all 768 Flow
assertions, including unchanged-source snapshot reconstruction, authored-origin
normalization, regenerated binding identity differences, metadata-aware
rehydration and the exhaustive persistence adapter fixtures. See
[fifth focused evidence](evidence/045-fifth-flow-tests.json). This establishes the
compiler/helper checkpoint and compilation of the saved Runtime snapshot slice;
public Flow authoring, durable publication and reload remain unverified.

The new dedicated Runtime acceptance project is registered in the solution and
Release validation script. Its first compile-only check used the preceding
fresh Core assembly solely to validate test source while Runtime edits
continued. It failed with 33 `FS0597` fixture syntax errors and nine reserved
identifier warnings; no Runtime behavior was executed. Those fixture repairs
are in progress. See [test-source build evidence](evidence/045-first-runtime-test-build.json).

The lifecycle slice is saved: persistent projection, replacement backup,
temporary task cleanup and discard restore or remove authored Flow word/case
objects by stable owner identity. Its diff check passed; behavioral rollback
and cleanup still require the dedicated Runtime suite.

The manifest writer and per-revision loader checkpoint compiled successfully.
The focused Storage suite passed all 14 groups and 228 assertions; see
[Storage checkpoint evidence](evidence/045-first-storage-tests.json). The one
removed assertion was the intentionally obsolete blanket Runtime Flow rejection,
not a schema or frozen-v1 oracle. The new loader dispatches from revision format,
parses historical source objects without pretending to reproduce their old whole
vocabulary, and compiles current heads before comparing the v2 export. Public
Flow routes and the replacement Runtime acceptance suite remain pending.

Runtime fixture compile attempts two and three corrected remaining indexed JSON
syntax/type errors; the fourth test-source-only build passed with zero warnings
and errors. These are fixture compilation results, not behavior results; their
complete diagnostics are retained as `045-*-runtime-test-build.json` evidence.

The first fresh Core/CLI/API build of the saved Flow define/eval route failed ten
diagnostics from two unannotated attachment source records. The worker is
repairing those record types before completing the source/describe/example read
paths and publication-state propagation. See
[fresh API build evidence](evidence/045-first-runtime-api-build.json).

The second fresh API build found four FlowHistory type-inference errors. After
explicit authored-word/map annotations, the third fresh build rebuilt Core,
CLI and the Runtime suite successfully with zero warnings/errors in 20.82
seconds. The first behavior attempt then failed at the first authored test:
the fixture used `durable.increment(...)` as namespace qualification, but Flow
correctly treats that as a receiver dot call. Qualified calls use `::`.
The fixtures are being corrected without changing language resolution semantics.
Evidence is retained in [second API build](evidence/045-second-runtime-api-build.json),
[third API build](evidence/045-third-runtime-api-build.json), and
[first Runtime attempt](evidence/045-first-runtime-tests.json).

The fourth fresh API build passed with zero warnings/errors in 20.75 seconds.
The second Runtime attempt completed the first durable define/commit/source-hash/
fresh-Engine/fresh-CLI group, then failed the migration fixture's baseline Stack
test-byte expectation. Existing Stack publication canonicalizes separate
expression lines and `=>` expectations; the fixture had assumed raw input bytes.
The historical-preservation oracle is being repaired to capture the actual
pre-migration authoritative bytes and require those exact bytes afterwards.
See [fourth API build](evidence/045-fourth-runtime-api-build.json) and
[second Runtime attempt](evidence/045-second-runtime-tests.json). The complete
Runtime suite and full Release gate have not passed yet.

The third Runtime attempt passed the preceding migration, replacement/CAS and
temporary/task lifecycle groups, then stopped at a snapshot fixture requesting
a named snapshot before a manifest existed. Storage intentionally requires an
active manifest for named snapshots. The fixture is being changed to a committed
baseline; this does not establish a Core snapshot defect or justify changing
the frozen snapshot format. See [third Runtime attempt](evidence/045-third-runtime-tests.json).

The fourth Runtime attempt exercised empty-authority task rollback but the new
fixture incorrectly expected the public word inventory to be empty; it includes
49 builtins. The oracle is being changed to compare the pre-task inventory and
require the added Flow owner to be absent. See [fourth Runtime attempt](evidence/045-fourth-runtime-tests.json).

Independent read-only Runtime review found no material correctness defect in
the saved vertical. It checked stable owner/revision rehydration, complete keyed
binding reconciliation, per-revision source dispatch, compile-before-activation,
publish-before-live-state activation, immutable history, authored read paths and
task/discard restoration. This review ran no tests and does not replace the
full acceptance gate.

The fifth Runtime attempt passed empty-authority rollback, then found a real
mixed-frontend defect: default Stack `eval` of virtual `file.write` after a Flow
commit returned `IR_SOURCE_ORIGIN_MISSING`. A legacy empty-origin compile path
lost the saved Flow source metadata. Runtime repair is in progress; source review
did not catch this defect, and its previous result is not release evidence.
See [fifth Runtime attempt](evidence/045-fifth-runtime-tests.json).

The repair routes Stack eval, attached Stack tests and Stack examples through
the source-aware Compiler APIs with the exact snapshot's Flow origins. A fresh
nonincremental Core/CLI build passed with zero warnings/errors in 18.99 seconds;
see [fifth API build](evidence/045-fifth-runtime-api-build.json). Mixed attachment
and reload regressions are being added before the next behavior run.

The sixth Runtime attempt found a second mixed-frontend defect: registering a
Stack wrapper of a committed Flow word returned `NAME_UNKNOWN_WORD` for that
Flow callee. The new regression requires Stack definitions/cases to share the
verified vocabulary before and after fresh reload. Repair is in progress;
see [sixth Runtime attempt](evidence/045-sixth-runtime-tests.json).

The repair skips provisional base-program builds only for structurally validated
empty source inventories. Nonempty retained inventories keep their independent
baseline checks; final whole-program compilation remains mandatory. The seventh
Runtime attempt passed the mixed Stack/Flow caller/test/example and fresh reload
regressions, empty-authority task rollback and populated snapshot/provider
restore. It then rejected tampered persisted bindings as intended, but the
fixture expected a word-only diagnostic rather than the more precise
`durable.increment/basic` attachment owner. That fixture is being corrected.
See [seventh Runtime attempt](evidence/045-seventh-runtime-tests.json).

The eighth Runtime attempt passed all 9 groups and 172 assertions; see
[focused pass](evidence/045-eighth-runtime-tests.json). Both compiler repairs
received independent read-only review with no remaining material finding.

The first complete Release gate rebuilt the solution with zero warnings/errors,
then completed all 26 checks. Only `language-acceptance` failed, in two existing
Stack reload groups: a historical expected-expression case resolved an old name
against the current vocabulary after rename, and the verified-IR snapshot group
found a canonical export mismatch. The other 25 checks passed. These compatibility
regressions are being repaired before publication. The gate and its supporting
projection/fixture/host/parser evidence are retained as
[first full gate](evidence/045-flow-runtime-validation.json).

Further diagnosis corrected the initial historical-case interpretation: Stack
rename rewrote an expected expression without advancing its owner revision when
the owner's word body was unchanged. Publication reused the old revision's test
refs, so strict reload exposed stale *current* test bytes. The repair must advance
owners of changed attached cases and preserve the earlier revision unchanged.
The export mismatch is a generated accessor test: generated words have no user
word-revision attachment slot in v2. Its compatibility repair must recover only
manifest-derived generated-owner cases from the hash-verified project object,
then still compile and byte-compare the full canonical export. Mixed Flow/type
case regression coverage is being added; no general aggregate-parser fallback
or manifest version change is planned.

Both Runtime compatibility fixes are saved. Stack rename now advances owners
when any attached test/example source changes and reruns the affected owners'
tests. Generated-type cases are recovered only for manifest-derived generated
owners from the hash-verified project object, with explicit Stack-section
selection in mixed exports and complete final export equality. V1 checks remain
unchanged. The exact failing local `language-acceptance` command now passes all
34 groups / 583 assertions after rebuilding Core; see
[compatibility rerun](evidence/045-second-language-acceptance.json). A mixed
Flow/generated-owner regression and the fresh complete gate remain pending.

The ninth Runtime attempt exposed a mixed-export section-selection bug in the
new generated-case bridge. The exporter separates a frontend marker and its
source with a blank line; splitting on blank lines discarded those Stack case
bodies. The repair selects complete marker-delimited bodies instead. Exact
source authority and final byte equality remain required. See
[mixed generated-case attempt](evidence/045-ninth-runtime-tests.json).

The tenth focused Runtime run passes all 10 groups / 189 assertions after the
marker-line extraction repair. See [expanded focused pass](evidence/045-tenth-runtime-tests.json).
The final complete Release gate is running against the frozen source. Flow
rename/deprecate guards and the default Stack route remain deliberate migration
limits; complete Flow maintenance and default authoring cutover remain required.

The complete rerun passed all 26 checks with a zero-warning/error solution build
in 16.49 seconds; see [second full gate](evidence/045-final-validation.json).
Before publication, the explicit v1 compatibility audit found that valid old v1
projects could also store generated-type cases only in their project source.
The prior loader preserved those cases through aggregate parsing. Recovery must
therefore apply to v1 too, limited to known generated owners and keeping v1's
semantic comparison unchanged. A v1-specific regression and final gate after
this compatibility repair are required before push; the 26-check pass above
predates that last repair.

The eleventh focused Runtime run passes all 11 groups / 203 assertions,
including the isolated Stack-only v1 generated-accessor test/example round trip.
See [final focused pass](evidence/045-eleventh-runtime-tests.json). The final
publication gate is running against this source. No native allocation, mailbox
runtime, performance benefit or controlled agent outcome is claimed.

## Publication validation

The final Release publication gate passed all 26 checks after the v1 repair,
with zero build warnings/errors (12.64-second solution build). Evidence:
[complete gate](evidence/045-publication-validation.json),
[fresh-process projection](evidence/045-publication-validation.projection.json),
[matched fixtures](evidence/045-publication-validation.matched-fixtures.json),
[external-subagent host verification](evidence/045-publication-validation.subagent-host.json),
and [parser limits](evidence/045-publication-validation.parser-limits.json).
The local artifact correctly reports a dirty checkout at the parent revision;
exact clean source-commit CI must be audited separately after push. Independent
review checked the final origin, inventory, rename and generated-case fixes;
behavioral and compatibility oracles remain the evidence for their execution.

Exact source commit `a24e9f5e201908b6ab1364331fa02e2ba8b0f31e` passed
[CI run 37417536333](https://github.com/benwmaddox/AgentLang/actions/runs/37417536333).
Its downloaded artifact identifies branch `prototype`, a clean checkout, all
26 checks passing, and zero build warnings/errors. The audited
[run identity](evidence/045-committed-source-ci.json) and
[full validation artifact](evidence/045-committed-source-validation.json), plus
its projection/matched-fixture/subagent-host/parser JSON companions, are saved.
Publication adds only reports/evidence and documentation after this tested
source commit; integration targets private `prototype` and `main` by fast-forward.
Post-publication CI is distinct from the exact source CI recorded here.
