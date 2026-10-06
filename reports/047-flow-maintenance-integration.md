# Flow-aware vocabulary maintenance

Status: local milestone validated; exact committed-source CI is pending. The full PRD remains the active
objective; this milestone does not replace default frontend cutover or controlled
external-agent evaluation.

## Verified starting point

The canonical checkout started clean on `prototype` at
`00076c11d1cd3780a789011983c031127d2c80ca`, shared by local and remote `main`.
The previous durable opt-in Flow milestone is integrated in the private repo.
Main publication [CI run 37418071591](https://github.com/benwmaddox/AgentLang/actions/runs/37418071591)
passed. Its downloaded artifact identifies that exact revision, branch `main`,
a clean checkout and all 26 checks with exit code zero. The audited
[run identity](evidence/047-parent-main-ci.json) and
[validation artifact](evidence/047-parent-main-validation.json) are retained.

## Implementation and validation plan

Add a pure `FlowRewrite` module after `FlowPersistence`, without changing the
semantic IR or storage format. Rewrite resolved calls by stable identity, using
explicit root calls for single-segment names and namespace-qualified calls for
dotted names, avoiding suffix lookup. Transform all descendant site paths; retain the expected
identity of untouched calls in the same changed document. Runtime reparses the
canonical source and compares the complete transformed binding oracle against
the compiled candidate before any publication.

Keep source changes separate from metadata-only deprecation. Advance a changed
word or attached case owner once, retaining all prior revision source objects.
Integrate the existing test, coverage, frozen-validator, durable publication,
task-abort and snapshot gates rather than introducing a parallel maintenance API.

Implementation ownership: pure rewrite module/project ordering; Runtime adapter;
pure rewrite acceptance; durable Runtime acceptance. These bounded tasks share
the canonical checkout with separate files. Root coordinates fresh builds and
reviews, documentation, evidence and private Git publication. No agent runs a
competing build.

Validation: fresh Release solution build, focused Flow and Flow Runtime suites,
Storage and language Acceptance, then the exact CI command
`pwsh -NoProfile -File scripts/Validate.ps1`. Audit unchanged v1 golden fixtures,
complete binding identity, evaluation order, own actual-site library coverage,
historical bytes and reload/rollback. Save failures as well as the final gate;
publish updated reports with source, then audit exact committed-source CI before
integrating private `main`.

## Acceptance contract

Implement existing `rename` and `deprecate` commands for Flow-authored words,
and renaming Stack words referenced by Flow sources. Rename preserves stable
identity, advances revisions, rewrites source by resolved identity and structural
AST path, and preserves receiver/argument execution order. Include ordinary,
root, dot and static callback references in definitions, tests, pure expectations
and examples. Changed attached cases advance their owner's revision too.

Compile the complete proposed snapshot and prove every old call still selects
the same stable target after explicit rewriting. Run affected tests, library
own-site coverage and frozen-validator checks before publication. Preserve exact
historical bytes, authored metadata, task rollback and snapshot restoration.
No parser fallback, dictionary-order rebinding or RPN rendering of Flow source.
Deprecation changes host metadata while retaining authored Flow semantics.

The read-only architecture and independent acceptance passes preceded source
edits. Runtime and AST rewrite implementation have separate file ownership;
the root serializes all builds and validation. Source/test implementation and
fresh validation are in progress; this report does not yet claim a passing
maintenance milestone.

## Pre-execution review

Review resolved the qualification distinction before implementation: absolute
root calls accept one-segment names; dotted destinations need namespace-qualified
ordinary calls/callbacks, with argument paths mapped to the chosen AST form.
Both forms use exact dictionary keys, and final binding comparison remains the
authority for preserved identity.

The first durable acceptance draft required fixture corrections before execution:
explicit `word` syntax for short static callbacks, literal-only examples,
allowed provenance actors, current-head revision selection rather than the first
historical name match, and the expected sum for the untouched-call negative case.
These are review findings, not passing test results or runtime defects. The
fixture owner is correcting them before the serialized fresh build.

Review of the first rewrite draft also found that its final inventory check
walked the original AST rather than the reparsed rewritten AST. The helper owner
is correcting this before execution. The final check must derive sites from the
canonical parsed result and compare them to the transformed complete inventory;
checking the old tree cannot establish that result.

## Validation attempts

The first fresh Core Release build failed with six errors and one warning in
the new helper: an unconstrained effect-role set and ambiguous F# record-field
inference in the per-document root selectors. No Runtime integration had landed
at this point. [Build diagnostics](evidence/047-first-core-build.json) are saved;
the helper owner is adding explicit types before the next build. This result
does not establish maintenance conformance.

After adding explicit F# set/AST types, the second fresh Core Release build
passed with zero warnings/errors (19.32 seconds). [Build evidence](evidence/047-second-core-build.json)
is saved. This compiles the pure rewrite module with the previous Runtime;
the new durable maintenance integration and acceptance suite are still pending.

The first fresh solution Release build compiled Core, the new Runtime adapter
and CLI, but failed in the two new test fixtures (17 errors / two warnings).
A path-prefix list was inferred as a source span, and revision assertion
parameters were inferred as word heads. [Solution build evidence](evidence/047-first-solution-build.json)
is retained. The test owners are adding explicit list/revision types; no
behavioral maintenance result is inferred from this partial build.

The corrected solution Release build passed with zero warnings/errors (5.02
seconds); see [second solution build](evidence/047-second-solution-build.json).
The first focused checks passed [Flow](evidence/047-first-flow-tests.json)
(832 assertions), [Storage](evidence/047-first-storage-tests.json)
(14 groups / 228 assertions), and [language Acceptance](evidence/047-first-acceptance-tests.json)
(34 groups / 583 assertions). The [Runtime suite](evidence/047-first-runtime-tests.json)
stopped at a test fixture that attempted to commit an untested competitor word.
Project words still need passing attached tests; only the full coverage gate is
lighter than for library words. The fixture owner is adding the missing cases.

Independent review confirmed a compatibility defect in the draft Runtime
adapter: changed Stack tests/examples retained old AST spans after the aggregate
legacy reparse was removed. The Runtime owner is reparsing each changed object
under its new hash-derived source label, preserving unchanged objects, before
the next fresh build. This keeps the mixed frontend boundary without leaving
diagnostics/source origins anchored to prior bytes. New source requires fresh
validation; the earlier focused passes do not cover this repair.

The changed Stack objects now reparse individually under source-hash-derived
labels; unchanged objects retain their AST. A fresh Core Release build after
that repair passed with zero warnings/errors (19.12 seconds). See
[third Core build](evidence/047-third-core-build.json). Runtime acceptance still
requires execution against the repaired fixtures; this build alone does not
establish maintenance behavior.

The third fresh solution Release build passed with zero warnings/errors
(13.20 seconds), recorded in [build evidence](evidence/047-third-solution-build.json).
The second Runtime execution reached `client.map` but rejected its fixture's
unavailable `first` dot stage (`FLOW_UNKNOWN_DOT_STAGE`); see
[Runtime evidence](evidence/047-second-runtime-tests.json). This is not a passing
maintenance milestone. The fixture owner is replacing that unsupported operation
with an existing typed operation and adding the bounded changed-Stack-case origin
regression before another fresh build. No source commit or push has occurred.

The README's five opt-in Flow JSON requests ran through the fresh Release CLI
in a new temporary project: all five responses succeeded, evaluation returned
42, and source inspection retained `value.add(1)`. [Smoke evidence](evidence/047-readme-flow-smoke.json)
is saved. The first harness assertion incorrectly expected `source.data.source`
instead of the protocol's string `source.data`; the runtime responses already
succeeded. [First harness evidence](evidence/047-first-readme-smoke.json) is kept
separately; the corrected shape assertion passed in a second fresh project.

The fourth solution Release build passed with zero warnings/errors; see
[build evidence](evidence/047-fourth-solution-build.json). The third Runtime
run progressed through the maintenance/restore and untouched-call rejection
fixtures, then stopped while committing `guarded.answer`
(`COMMIT_TESTS_FAILED`), recorded in [Runtime evidence](evidence/047-third-runtime-tests.json).
Inspection confirms tests start with an isolated empty virtual filesystem;
live evaluation seeds cannot alter test providers. The fixture assumed otherwise.
Keep isolation intact: use explicitly different fixed-clock providers in fresh
Engine instances to test current-provider test failure and branch-coverage loss.
The fixture owner is repairing this setup. No full-suite pass is claimed.

After the clock-provider fixture repair, the fifth fresh solution build passed
with zero warnings/errors ([build evidence](evidence/047-fifth-solution-build.json)).
The fourth focused Runtime run passed all 15 groups and 399 assertions
([run evidence](evidence/047-fourth-runtime-tests.json)), including the clock-based
failure and lost-library-coverage gates. This is focused evidence, not the full
publication gate. A bounded public Stack changed-case source-hash diagnostic
regression is being added for the confirmed stale-location repair, followed by
another fresh build and the full 26-check gate.

The proposed direct changed-Stack-case location regression was not added:
Stack lookup is exact, public test/example results omit spans, and `ir` exposes
word bodies only. The span repair has independent static review and fresh build
evidence, plus the surrounding lifecycle suite; those do not prove a direct
public case-location assertion. Keep this diagnostic observability limitation
visible for the structured-error work rather than add a vacuous oracle.

## Final local validation

The full Release gate passed all 26 checks with zero build warnings/errors.
[Validation evidence](evidence/047-publication-validation.json) records the
uncommitted source at parent revision `00076c1` as dirty, rather than claiming an
exact committed-source result. Companion artifacts cover persistence projection,
matched fixture contracts, the external trial host, and parser process limits.
Flow passed 832 assertions; Flow Runtime passed 15 groups / 399 assertions;
Storage passed 14 groups / 228 assertions; language Acceptance passed 34 groups /
583 assertions. The frozen v1 compatibility fixture still passes; semantic IR
and storage schema versions were not changed.

The implementation preserves stable call targets across rename, keeps every
retained binding in changed documents, advances attachment owners once, and
uses the existing test/library coverage and atomic publication gates. History,
rollback, snapshots and fresh CLI reload are exercised. Flow remains opt-in.
Default full authoring cutover, broader providers/domain support and actual
controlled external-agent comparisons remain open; these checks establish
runtime behavior, not a token, latency or memory benefit.

Publication will push the source with these reports, audit CI against that
exact commit, then integrate a report-only CI evidence update into main.
