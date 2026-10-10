# Coordinated function replacement staging

Status: final focused runtime validation and all 37 full local Release checks
passed after an earlier run was interrupted for review fixes. This is engineering needed
for the next maintenance comparison, not a new agent efficacy result.

## Problem and behavior

A signature change and its callers could not previously be staged together:
each one-function edit compiled the entire dictionary against the other old
definitions. A six-to-seven-input edit failed whichever function was edited first.

Multi-declaration Flow `define` now accepts `replace: true` with an exact
`expectedRevisions` map. Each declared function must be an existing persistent
authored Flow function at the observed revision. The operation preserves its ID
and maturity, advances its revision, retains its original replacement backup,
and checks the complete proposed dictionary before activating any candidate.
The existing one-function scalar CAS route remains separate.

Inline standalone tests/examples replace matching cases. Omitted cases and
test-file wrappers remain attached, with their source bindings checked and owner
revision metadata advanced. Retained cases must still type check; a stale test
calling the old signature rejects staging. New types/functions, temporary changes,
attachment removals, wrapper edits and external attachment arrays are excluded
from this initial replacement batch.

Publication uses the existing selection, test and library gates. Selecting a
persistent replacement through ordinary commit aliases also expands its callers.
Staged affected callers are selected using both old and proposed dependencies.
Independent batch members outside that selected closure remain staged; this is
atomic staging, not automatic publication of arbitrary unrelated replacements.
No semantic IR, native ABI or durable manifest version is changed.

## Evidence so far

The fresh Release runtime-test build passes with zero warnings/errors. The
runtime suite passes 39 groups / 1,507 assertions, including:

- Six-to-seven-input migration with two persistent library callers, stable IDs,
  advanced revisions, retained standalone/wrapped cases and examples, fresh reload.
- Stale revision and invalid source rejection without candidate or durable drift.
- A newly introduced caller edge included in replacement publication.
- Targeted commit aliases and replacements selected through metadata dependencies
  enforce the same caller gate; deliberately removed old edges can publish.
- Failed caller assertions, incomplete Bool branch/input evidence, incomplete
  finite Result returns, and unqualified authored dependencies blocking acceptance.
- Task commit followed by abort restoring the original durable manifest.

Independent review identified the new-edge selector gap: using only backup
dependencies missed a staged caller that newly depended on the selected callee.
Both expansion loops now consider old and current dependencies, with a regression.
Final review also found that this selection must apply when a targeted ordinary
commit selects a staged persistent replacement, including one selected through
test/example dependencies. It cannot depend solely on the chosen command alias.
Targeted commit regressions have passed in the focused suite. An attempted
old-runtime comparison captured an already updated binary and therefore showed
the new rejection; it is not evidence of the old behavior.

A subsequent implementation added an old-caller/new-callee hybrid snapshot to
preserve an earlier fixture's expectation. Final simplification removes that
extra machinery: selected replacement revisions must pass their own tests;
unselected retained callers must pass theirs against the proposed dependency.
An intentionally replaced caller that removes an old dependency must not be
forced to run its obsolete body against the new signature. The final focused
checks validate this behavior, including a six-to-seven-input migration with an
updated caller that no longer calls the changed function.

Initial implementation/fixture failures are retained in the validation log:
F# inference and syntax fixes, a missing syntax selector, confusion between Flow
test projections and Stack attachments, the examples response array shape,
unchanged tests incompatible with new arities/return types, and an invalid
primitive name. These are coordinator development failures, not participant
outcomes or efficacy evidence.

The initial full serial gate used workspace-local TEMP/TMP and package auditing
explicitly disabled for local offline validation. Its fresh build and completed
checks passed, but the run was interrupted before completion for the review fix;
it is not a full passing gate. The final complete serial Release gate passed all
37 checks, including a fresh build, runtime 39 groups/1,507 assertions and the
older business-policy preflight's 98 checks across 30 independent outcomes.
Package auditing remained disabled and TEMP/TMP remained workspace-local.
No CI run was used.

The [verified evidence archive](evidence/165-atomic-function-replacement-staging/archive.json)
records 25 files including final full validation and retained initial failures.
ZIP SHA-256: `9966612cc60aafabcde4c03746553f3a2a853742270fdaff23152ed3598d75e5`.
All entries were verified; build binaries and temporary project trees are excluded.

## Next comparison

The proposed maintenance task adds a trailing dry-run Bool to report 163's actual
accepted handoff implementations and updates two coordinator-created callers.
All cancellation and replacement validation must still execute. Successful dry
runs return the original complete store; other successful calls return the
updated store. The independent existing 33-case model is evaluated in both modes,
with retained caller checks separate. Both seeds and behavioral controls have now
been calibrated. Correct controls pass 66 target checks in each environment;
the Flow grader also passes 66 retained-caller checks. Two fully executing faults
are rejected in both environments: skipping creation validation has 16 dry-run
failures, and returning a modified store has 10. These are calibration outcomes,
not fresh participant outcomes. Controls do not stand in for library qualification;
the actual Flow participant starts with the accepted library implementation and
must satisfy its gates. Inputs were frozen and both fresh participants completed
against their pinned runtimes; outcomes are recorded separately in
[report 166](166-handoff-signature-maintenance-agent-pair.md).
The full gate's older business-policy
preflight was still pending at dispatch, after the focused runtime, CLI, IR,
persistence and broker checks had passed. That timing is recorded in the freeze.
The full gate subsequently passed before committing this milestone.
