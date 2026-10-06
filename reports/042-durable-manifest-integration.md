# Durable manifest v2 integration

Status: local and exact committed CI acceptance passed. The full 82-section PRD remains active.

## Baseline and progress

The previous goal turn changed authoritative state: source milestone `d0782c1`
passed exact clean committed CI (25 checks, 705 Flow assertions), and reports-only
publication `3c52ee8d0f7f527f9f6c19548491fb79fad97aaa` was fast-forwarded into
private main/prototype. The canonical checkout is `D:\code\AgentLang`, branch
`prototype`, initially clean. No worktree is used. Publication main CI run
37399560917 was observed live at the start of this turn and then completed
successfully: [saved identity](evidence/042-parent-main-ci.json).

## Plan and ownership

Implement the schema boundary already specified in
[the durable Flow plan](../docs/FLOW-DURABLE-INTEGRATION.md). Immutable authored
word/test/example objects remain authoritative. Manifest v2 records an explicit
Stack/Flow grammar version on every revision and canonical stable call bindings.
Bindings retain exact source membership, case/body-role/structural-path identity,
call form and target identity, without persisting a target revision or an IR
ordinal as a rebinding key. Runtime/compiler will later prove semantic completeness.

The Storage owner (Luna/max) owns Storage.fs, minimal Runtime constructor and
unsupported-frontend guard changes, the durable plan and report 043. The acceptance
owner (Luna/max) owns Storage.Tests Program.fs and report 044. Root owns this
integration report, requirements ledger, serial validation and publication.
Shared checkout ownership does not overlap. No agent builds concurrently.

Keep CURRENT and named snapshot envelopes at v1. Manifest readers accept only
v1/v2 and inspect the enclosing version before version-specific fields. V1
parsing synthesizes Stack v1/empty bindings; v1 serialization omits new fields
only when those defaults hold and otherwise rejects loss of meaning. Preserve
all six frozen v1 hashes and every historical source reference. New Runtime
publication uses manifest v2 with its currently Stack-authored revisions. The
lower-level Storage API still accepts an
explicit v1 manifest for compatibility and exact byte-preserving round trips;
it never silently upgrades or discards meaning-bearing fields. Until
the complete authored Flow load path is implemented, Runtime explicitly rejects
Flow metadata before legacy parsing instead of activating an incorrect projection.

## Acceptance and validation

Require deterministic typed/wire round trips; compatible old history under the
same word ID; v1 snapshots restoring v2 manifests; structured malformed-format,
membership, duplicate-key, closed-tag and resource-bound failures; unchanged
authority on rejected publication; and frozen literal v1 bytes/hashes.
Binding metadata stays inside the existing 8 MiB metadata limit with an explicit
20,000-entry bound, path depth 128 and bounded indexes/identity text. Canonical
sorting is serialization order, not execution order.

After source and fixtures freeze, root runs a fresh Core build, focused Storage
acceptance and the complete Release gate:

```powershell
dotnet build src/AgentLang.Core/AgentLang.Core.fsproj -c Release --no-incremental
dotnet run --project tests/AgentLang.Storage.Tests -c Release
pwsh -NoProfile -File scripts/Validate.ps1 -Configuration Release -ReportPath .agentlang/reports/042-durable-manifest-validation.json
```

Save actual outputs and failures before fixes. Publish source and updated reports
to the private prototype branch, audit exact committed CI artifacts, then publish
that proof and integrate main by fast-forward. No result is claimed before it is
observed. This schema step does not implement Flow Runtime/parser dispatch,
default authoring, mixed exports, rename/rollback/library gates, complete business
vocabulary, providers or controlled agent-performance experiments.

## Independent planning review

Read-only Luna/max review confirmed the version-ordering trap: the old reader
decoded source references before rejecting an unsupported schema, while its
existing future-version fixture had no revisions and therefore could not detect
that ordering. New malformed future-version fixtures require version rejection
to win before source/reference/revision parsing. The review also confirmed the
shared envelope constant, v1 default/omission checks, mixed-version snapshot
fixture, same-ID historical reference preservation and bounded keyed binding
validation needed by this milestone. These are identified acceptance gaps, not
claims of passing implementation.

## First source checkpoint

Storage and Runtime were frozen before root's fresh `--no-incremental` Core
build. It passed with zero warnings/errors in 16.77 seconds. Exact command,
dirty baseline revision, source hashes and full output are saved in
[first build](evidence/042-first-core-build.json). This proves compilation of
the source checkpoint, not migration or execution acceptance. Program fixtures
are finishing their separate static review; independent source review is active.

The first focused Storage attempt did not compile the new acceptance fixtures:
F# required parentheses around indexed/method arguments and consistent record
update indentation. Full output is preserved in
[first focused failure](evidence/042-first-focused-storage.json). Core source
remains frozen; fixture repairs retain the planned assertions. No passing Storage
execution is inferred from the source build or these compile diagnostics.

Read-only review of the frozen Storage/Runtime implementation found no material
source defect. It checked version-first dispatch, mandatory v2 metadata and v1
default/omission guards, resource preflights on wire and DTO paths, closed forms
and targets, keyed source membership/uniqueness, canonical ordering, v1 envelope
preservation and Runtime guard-before-parse behavior. Fixture review follows
their compile repair; this source review alone does not prove execution acceptance.

The second focused attempt found two remaining indexed-receiver syntax errors
in fixture mutations. Its complete output is preserved in
[second focused failure](evidence/042-second-focused-storage.json). Core remains
unchanged; only test syntax is being repaired.

The third focused attempt reached fixture type checking and found unqualified
`FlowAstPath` constructors plus implicitly ignored commit results. Saved
[third focused failure](evidence/042-third-focused-storage.json); the acceptance
owner qualified constructors and made result discards explicit. A static source
check also corrected the v1 unsupported-syntax-version fixture to expect
`STORAGE_UNSUPPORTED_VERSION`; valid Flow metadata and bindings remain separate
v1 nonrepresentable-meaning rejection cases. No Core change was required.

The fourth attempt compiled and executed into the migration group, then found
that a fixture compared its unsorted input binding list to canonical loaded order.
All revision fields and binding rows matched; only list order differed. Saved
[fourth focused failure](evidence/042-fourth-focused-storage.json). Metadata
comparison will normalize complete binding rows independently, while the separate
canonical-byte ordering assertion stays intact. Persisted list order is not
execution order; no source fix is called for by this mismatch.

The fifth attempt passed migration and snapshot checks, then exposed a fixture
error-category mismatch for a missing required source-format object. Existing
Storage `require*` helpers classify missing/wrongly shaped wire fields as
`STORAGE_INVALID_JSON`; explicit schema semantics use `STORAGE_INVALID_MANIFEST`.
The fixtures will follow those existing structured categories rather than
changing Core's wire convention. Complete output:
[fifth focused failure](evidence/042-fifth-focused-storage.json).

The sixth run reached the Runtime publication fixture after the schema,
migration/snapshot and limit groups. Its untyped Stack declaration was not a
valid candidate; the fixture must provide a signature, declared effects and an
attached passing test before commit. Saved
[sixth focused failure](evidence/042-sixth-focused-storage.json).

Read-only fixture review also identified non-vacuity gaps to fix before final
acceptance: the v1 nonempty-binding fixture used foreign references, source-role-
case incompatibility was not directly tested, the future-version fixture omitted
all version-specific fields, and the Runtime guard covered current Flow but not
historical Flow under a Stack current head. Those cases are being strengthened,
with bounded text negatives added alongside existing path/count limits. This is
additional acceptance work, not a claimed source defect or a weakened gate.

The seventh attempt stopped at a missing closing list bracket in a revised
fixture and offside multiline Runtime source construction. The Stack test text
also used Flow's `=>` marker instead of Stack's `expect`; the fixture repair uses
the established explicit Stack grammar and simpler source construction. Full
output is preserved in [seventh focused failure](evidence/042-seventh-focused-storage.json).
These fixture mistakes are not controlled agent-language experiment outcomes.

The eighth attempt found a remaining offside nested head-record update in the
expanded fixture: [saved failure](evidence/042-eighth-focused-storage.json).
The repair flattens that update into a named head value. Root also strengthened
the guard-order proof: an empty aggregate parses successfully and can only cause
a later project mismatch, so the mixed-history fixture now needs deliberately
invalid Stack aggregate text with exactly matching export bytes. With a Stack
current head and a Flow historical revision, `RUNTIME_UNSUPPORTED_FRONTEND` must
win before parsing. This is a guard-order fixture, not a semantically valid mixed
Flow Runtime project. No Core source changes were required.

## Focused acceptance

The ninth focused run passed **14 groups / 229 assertions**, exit code zero:
[saved output](evidence/042-ninth-focused-storage.json). The complete fixture
coverage includes frozen v1 bytes/defaults, version-aware refusal, canonical v2
bindings, preserved same-ID historical sources, v1 snapshot envelopes referencing
v2 manifests, malformed schema/member/role/path/target metadata, text/count/depth/
aggregate-path limits, explicit Stack-only Runtime v2 publication and historical
Flow rejection before an invalid aggregate can reach Stack parsing.

All eight failed attempts remain preserved above. Core source is unchanged since
its fresh passing build. Root's full Release gate is now running against the
final frozen source and fixtures; its result and final fixture review are still
required before milestone publication. Storage schema validity is not proof of
Flow Runtime semantics or agent performance.

## Integrated local acceptance

The final frozen checkpoint passed the complete **25-check Release gate**:
[saved gate](evidence/042-durable-manifest-validation.json) and its four companion
files. Every check exited zero; the fresh solution build had zero warnings/errors.
This includes the unchanged 705-assertion Flow suite, 31-check binding probe,
Storage's 229 assertions, existing Runtime/library/rollback acceptance, persistence
projections and parser limits. Existing trial/task-bank checks are deterministic
fixture/harness validation, not a new live agent experiment or measured outcome.
The local record identifies the dirty baseline checkout and is not committed CI.

Final read-only review found no material source or fixture issue. It rechecked
the non-vacuous v1 metadata refusal, both mandatory v2 fields, Stack v2 binding
refusal, role/kind/case and identity text checks, future-version precedence and
the mixed-history guard before parsing malformed aggregate text. Source and test
files remain frozen for publication; reports record the failed attempts as well
as the actual successful evidence.

## Committed CI and publication

Source milestone `15534ce801bbf756b287e2e321afa6bc60ed2da8` passed
[exact committed CI](https://github.com/benwmaddox/AgentLang/actions/runs/37404798239).
The downloaded artifact identifies that exact revision, a clean checkout and all
25 successful checks, including Storage's 14 groups / 229 assertions. Saved
[run identity](evidence/042-source-ci-run.json),
[gate](evidence/042-source-ci-validation.json) and four companion records.
Repository privacy was checked before source publication and again before main
integration. This reports-only publication changes no executable files after CI.
Main/prototype integration uses a fast-forward without a force push or worktree.

The next required work is authoritative authored-source state and manifest-selected
Flow Runtime parsing/compilation, deterministic mixed export assembly, semantic
binding validation, edit/rename/library gates, fresh-process rollback/snapshot
conformance and eventual default authoring cutover. Providers, business vocabulary
and controlled external-subagent experiments remain in the full PRD scope. This
schema milestone does not establish productivity, context or memory benefits.
