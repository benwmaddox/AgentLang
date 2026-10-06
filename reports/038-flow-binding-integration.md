# Flow authored binding integration

Status: source-backed compilation passes the final focused Flow suite (564 assertions),
independent probe (31 assertions) and final complete 25-check Release gate.
Independent final review closed the baseline semantic/parameter guard gaps.
Committed CI and publication are tracked below. The complete PRD remains active.

## Current authoritative baseline

The previous turn delivered word-batch compilation through body-free signature
resolution and one final real-body verification. Source commit 0db29dd passed
local and exact committed CI with all 24 Release checks; reports-only publication
574eeba was fast-forwarded to private main/prototype. Its post-publication main
run 37383508098 is live when this report begins. The canonical checkout is used;
no worktrees are created.

That post-publication main run subsequently passed for exact publication commit
574eebaf8514678672b3c032cb250ba1ba5c8be0; saved
[run identity](evidence/038-parent-main-ci.json). This confirms the parent
milestone's post-merge gate, not the new binding implementation.

The next requirement is structural authored call bindings reconciled with final
IR and unchanged-source re-resolution. IR ordinals and span-only lookup must not
merge distinct call sites. Stable target identity and target revision have
different roles: editing the same word preserves identity, while redirecting to
another word must be detected. Authoritative source-kind/hash/revision scoping
and parser/byte validation remain part of durable integration.

The worker has started the approved lowering refactor; the early build and
regression checkpoint is recorded below. Independent review checked
whether emitted call-event order reconciles faithfully with verified IR across
arguments, receivers, branches, cases, generated operations and callbacks.
Implicit scalar validator calls must not become invented authored call sites.

## Approved implementation and acceptance

Preserve the existing AST batch API and add a source-backed companion. Current
Flow inventory and changed documents supply logical files, exact source refs/
text and expected owner name/identity/revision. Validate kind/hash/ownership,
parse those bytes, and prove base sources match existing metadata and normalized
real bodies before accepting them as current sources. Completeness is relative
to the host-declared current Flow owner set: current Context has no frontend tag.
This transient proof does not authenticate manifest membership. Later Runtime
and manifest integration must provide the authoritative complete set.

The approved public companion name is `compileBatchFlowSources`, with
`FlowSourceDocument`, `FlowSourceInventory`, `FlowSourceChange` and
`FlowBoundBatchCompilation`. Binding rows use `FlowAstPathSegment`/`FlowAstPath`,
`FlowCallForm` and `FlowCallTargetIdentity`. The word-source slice does not yet
assemble attached test/example refs. Reuse batch preparation/checks without
compiling an intermediate changed-only program; baseline verification is
separate, then one final proposed-program compile includes every real body.

`compileWordWithCallBindings` is approved as a thin, distinct transient AST
result sharing the capture/reconciliation core. It permits a regression with
multiple calls in one AST using identical spans and distinct structural paths;
it makes no SourceRef or source-byte authenticity claim. The source-backed
companion still parses only verified supplied text. Do not hide extra final
compilations behind this helper.

Capture structural AST role/index paths only during call emission. Keep bindings
as per-child fragments composed alongside emitted Expr fragments, not a mutable
global append order. Review found that current If/match construction visits arms
before conditions/scrutinees, while emitted code and IR visit the condition/
scrutinee first. Fragments preserve existing temporary/marker allocation and
permit identical spans without merging sites. AuthoredSpans sets and regenerated
IR ordinals are not durable identities.

Reconcile exact call-event coverage against an explicit recursive IR call-like
walk, including generated constructor/accessor/scalar and callback operations,
excluding implicit scalar validators. Verify owner, source span, target kind/ID
and exact current revision. Re-lower retained source under the proposed catalog
without bumping owner revisions or changing status/maturity; compare structural
paths and stable target identities. Reject redirection, ambiguity or missing
sites with caller/path/span information before returning a proposed context.

Acceptance covers all AST child roles, duplicate host spans, calls in conditions/
scrutinees and arms, nested named arguments/receivers, root/dot/static callbacks,
generated calls and validator exclusion. Add wrong/incomplete/duplicate source
inventory, wrong kind/hash/ownership/revision/body cases; same target ID at a new
revision remains valid while changed identity fails. Preserve non-Flow entries.

The Luna/max worker owns FlowLowering, narrowly scoped FlowSyntax binding/path
types if needed, focused Flow tests, the project-lowering doc and report 037.
Root owns durable-plan corrections, requirements, this report, serial builds and
publication. Independent Luna/max review owns read-only ordering/coverage audit.
All share the canonical checkout with no worktrees or overlapping writes.

After Core freeze run a fresh Release Core no-incremental build and focused Flow
suite, then:

```powershell
pwsh -NoProfile -File scripts/Validate.ps1 -Configuration Release -ReportPath .agentlang/reports/038-flow-binding-validation.json
```

Save failures and final evidence, update reports before committing/pushing to the
private repository, and verify exact committed CI before fast-forwarding main.

## Persistence prerequisite correction

Root inspected the actual literal v1 fixture inventory and the current validated
compiler APIs, then corrected the durable integration plan's stale prerequisite
status. The v1 golden CURRENT/manifest/source objects exist and current storage
validation passes 9 groups / 105 assertions; Flow-authored attachments and word
batch lowering also exist. V2 compatibility, complete attachments/bindings and
Runtime/default authoring do not. This documentation correction changes no
storage format or executable behavior. Validation is `git diff --check`.

Root also audited the concrete storage migration boundaries: the shared private
version constant currently governs manifest validation, CURRENT pointers and
named snapshots, while Runtime independently hard-codes manifest version 1.
The durable plan now maps those serializers/parsers and requires a version-1
snapshot to restore a version-2 manifest without upgrading the snapshot format.
F# file order prevents Storage from depending on FlowLowering binding or IR
types; storage-neutral DTOs need explicit semantic validation at the later
Runtime/compiler boundary. This inspection adds migration acceptance criteria,
not implemented v2 compatibility or a runtime validation result.

## Early Core build checkpoint

The owner froze Core after fragment/path capture and the transient bound-word
API, before source-backed batch assembly or focused tests. Root ran
`dotnet build src/AgentLang.Core/AgentLang.Core.fsproj --configuration Release --no-incremental`.
It failed with 15 errors and one warning: unqualified cases on the qualified
`FlowAstPath` union, a missing recursive group for semantic-body normalization,
and one attachment caller still passing a lowered fragment where expressions
were required. Saved [raw failure](evidence/038-early-core-build-failure.json).
The owner is repairing those compile issues before the next frozen checkpoint;
there is no passing new-slice build or test result yet.

The second fresh Core build removed the earlier 15 errors and warning but
failed on two branch-path constructor applications lacking parentheses around
their integer payload. Saved [second failure](evidence/038-early-core-build-second-failure.json).
The owner is correcting that application syntax before another fresh build.

The third fresh Core Release build passed with zero warnings/errors, exit code
0; saved [build evidence](evidence/038-early-core-build-success.json). While Core
remained frozen, the existing focused Flow suite passed 473 assertions, exit
code 0; saved [regression evidence](evidence/038-existing-flow-regression-pass.json).
This validates compilation and existing Flow behavior after the fragment
refactor. It does not prove new binding acceptance, retained-source stability or
complete project assembly. The owner resumes source-backed assembly and new
fixtures next; full integration and publication remain pending.

Independent read-only review found no material ordering/identity defect in the
checkpoint capture and reconciliation. It verified receiver and written-order
argument events before enclosing calls, condition/scrutinee events before arm
events, and generated-operation matching with implicit validators excluded.
However, the existing 473-assertion Flow suite does not invoke the new bound-word
API. New feature acceptance must cover same-span different-target nodes within
one host AST, all branch/match roles, named dot arguments, destructuring/returns,
static callback spans and implicit scalar validator exclusion. A separate
Luna/max probe owns `scripts/Verify-FlowCallBindings.fsx`, using the freshly built
checkpoint assembly without building or editing live Core; its result is not yet
claimed here.

Independent read-only storage review confirmed the migration seam: manifest-owned
source-format and ordered binding DTOs containing source refs, structural paths,
target kinds and stable string identities. Persist stable target identity rather
than pinning a target revision; final IR reconciliation still verifies the exact
current target revision. Storage owns shape/reference/ownership/limit checks;
Runtime owns exact site completeness and verified resolution. The review also
requires unsupported-version rejection before version-specific fields and rejects
Flow metadata in a v1 serializer rather than silently omitting it. These are
recommendations for the later storage implementation, not a completed schema.

## Source-backed assembly checkpoint

The owner froze the new `compileBatchFlowSources` implementation before adding
focused cases. It validates exact declared owner inventory, strict UTF-8 source
refs and base metadata, checks a separate base snapshot, re-lowers unchanged
Flow owners under proposed signatures and compares target identity, then
assembles all real bodies for final compilation/reconciliation. The source label
must match the original base definition's file label so IR span evidence is not
silently normalized; this remains a host-supplied diagnostic/provenance label,
not independent manifest authentication.

Its first fresh Core build failed on two F# interpolated-string syntax errors
in a diagnostic formatter. Saved [source-backed build failure](evidence/038-source-backed-core-build-failure.json).
No source-backed execution pass is claimed. The owner is repairing formatting
before the next build; independent review now audits inventory/identity/origin
and retained-source checks.

The next fresh build reached type checking and failed with 16 errors/one warning
from partially applied diagnostic formatting, ambiguous map/record inference,
and a missing intermediate parsed-change DTO. Saved [second source-backed failure](evidence/038-source-backed-core-second-failure.json).
These compile issues are being repaired before feature execution.

Root source review also found that the new replacement path required every
target to already have a base Flow source document. That would reject the
required Stack-to-Flow revision migration under the same stable ID. The owner
was asked to validate replacement identity/revision against the immutable base
for all user words, require Flow inventory proof for existing Flow owners, and
permit authenticated new Flow source for a non-Flow target. Acceptance must
exercise this transition and retained stack caller verification. This is a
source finding pending repair/reproduction, not a passing migration result.

The owner repaired the compile errors and the Stack-to-Flow boundary, then froze
Core again. The third source-backed fresh Release build passed with zero
warnings/errors, exit code 0; saved [build evidence](evidence/038-source-backed-core-build-success.json).
The existing Flow regression suite then passed 473 assertions, exit code 0;
saved [regression evidence](evidence/038-source-backed-existing-flow-pass.json).
The replacement repair remains unproven by a new transition fixture at this
checkpoint. New focused binding/source/stability acceptance is next.

The independent probe passed 10 assertions after root reviewed its source and
reran it against an exact copied build artifact. Saved [probe evidence](evidence/038-duplicate-span-probe-pass.json).
The loaded Core SHA-256 is
`43555fb9119bf94e15c1575adb4b7f5e6604f1450ce3dbb4ef7c2e3d0e7bda59`.
It constructs six calls in one host AST with identical spans, asserts exact
distinct structural paths and condition/then/else target order, independently
walks verified IR and executes the true branch to the expected integer 15.
It excludes implicit scalar validators from its authored-call walker. This is
transient AST proof, not persisted-source authenticity or source-inventory proof.

The first attempt could not run because a no-incremental build had removed the
earlier Release DLL before failing; no older artifact was substituted. The
successful new Core DLL was copied to an ignored content-hash directory before
execution. The portable tracked script takes its assembly through FSI's
`--reference` argument. Root added it to `Validate.ps1` after the solution build
with that gate's configuration-selected Core DLL; the resulting full gate has
25 checks. No complete 25-check pass is claimed yet.

Root reviewed and reran the extended pinned probe: 31 assertions passed, exit
code 0; saved [migration probe evidence](evidence/038-stack-flow-migration-probe-pass.json).
The additional fixture supplies a verified host-built stack body and retained
stack caller, then replaces the owner with exact-hash parsed Flow source under
the same stable ID from revision 1 to 2. Status/maturity and retained caller
definition remain unchanged; final caller IR uses revision 2 and executes to 42,
while the original base still executes to 41. Wrong new-source hash returns
`FLOW_SOURCE_HASH_MISMATCH` and leaves that base unchanged/executable. This
proves the source-backed compiler transition repaired above. The old stack
SourceText is a host fixture label, not a parsed v1 object; this does not prove
historical source-object preservation, durable migration or library test gates.

## First expanded focused suite

The owner froze Core and Program after adding source/inventory/transition tests,
stable-ID redirection, root stability, named dot-argument ordering, callback
identities and generated record/scalar cases. Root ran the focused Release Flow
command. It failed before test execution on an F# fixture binding named `base`,
which is reserved, and its use. Saved [focused build failure](evidence/038-first-focused-flow-build-failure.json).
The fixture owner is renaming that local before the next run; no expanded-suite
execution pass is claimed yet.

After renaming the reserved local, the second focused build failed on
unqualified nested `FlowLowering` types/cases and ambiguous `Definition` record
labels in fixture builders. Saved [returned diagnostics](evidence/038-second-focused-flow-build-failure.json);
the tool output was truncated and that evidence explicitly records the limit.
The owner is qualifying types and adding record annotations. Core stays frozen;
these are fixture compile repairs, not altered production guards.

The third focused attempt built successfully and began execution, then failed
while seeding the redirection fixture: `client.answer` itself matched the short
callee name `answer`, making the baseline ambiguous with `domain.answer` before
the intended vocabulary change. Saved [first execution failure](evidence/038-first-focused-flow-execution-failure.json).
The caller needs a noncolliding name; the resolver's ambiguity guard remains
unchanged. This is not a completed redirection test.

The fourth attempt reached the vocabulary mutation but correctly returned
`FLOW_AMBIGUOUS_CALL`: root's earlier fixture suggestion incorrectly assumed
exact-name precedence for ordinary short calls. The resolver considers all
applicable short-name candidates. Saved [second execution failure](evidence/038-second-focused-flow-execution-failure.json).
The revised trigger replaces the old target's signature while adding a new
compatible target in the same proposed batch, so unchanged source can resolve
successfully to a different identity. The guard must then report
`FLOW_CALL_REBOUND`. Assertions inspect the diagnostic's structured Expected/
Actual path and identity data. No production resolution rule is changed.

The fifth run reached the structural-role fixture and failed because the root
call argument was only a local value; no call event occupied that argument path.
The fixture now uses a nested root call. Saved [root-path fixture failure](evidence/038-focused-flow-root-path-fixture-failure.json).
The sixth run then failed on an ordering selector that chose the first callback
argument anywhere in the body instead of the child under the tested dot stage.
It now selects exact receiver/argument paths beneath that stage and checks their
target identities and order. Saved [dot-order fixture failure](evidence/038-focused-flow-dot-order-fixture-failure.json).
Both corrections make the assertions substantive; Core guards remain unchanged.

The seventh focused run passed 555 assertions, exit code 0; saved
[focused success](evidence/038-focused-flow-success.json). Core/Program are frozen
for final independent fixture review and the full 25-check Release gate. The
scope includes definition-source binding/identity stability, not attached case
bindings, persistent v2 source or Runtime/protocol cutover.

## First complete local gate and final acceptance refinement

The complete local Release gate terminated with exit code 0 and all 25 checks
passing, including zero build warnings/errors, Flow 555, lint 68, IR 102,
interpreter 22, language 34 groups/583, storage 9 groups/105 and the 31-assertion
probe loaded from this gate's fresh configured Core output. Fresh-process
persistence, matched fixtures, trial host, parser limits and whitespace checks
also passed. Saved [full gate](evidence/038-flow-binding-validation.json) and
the four adjacent evidence files. They identify a dirty prototype checkout
based on `574eebaf8514678672b3c032cb250ba1ba5c8be0`, not committed CI proof.
Task-bank validation remains shape-only: 2,114 assertions, 60 tasks and 180
proposed vectors do not establish outcomes for those tasks.

Final independent review found no material Core defect and confirmed the
high-risk fixtures are substantive. It identified two remaining baseline proof
branches unexercised by current negatives: semantic-body comparison and valid
same-arity parameter-name mismatch. The current changed-source case is rejected
earlier by exact byte comparison. The owner is adding valid-host-context
mutations that preserve source/ref/identity/revision while changing the body or
parameter catalog, plus an optional missing-catalog case. A new focused run and
full gate will follow those changes; the earlier successful evidence is retained.

## Final local acceptance

The added valid-body, renamed-parameter and missing-parameter negatives passed
in the final 564-assertion Flow run. Final independent review confirmed they
reach the intended guards and found no remaining scoped issue. The final
Release gate passed all 25 checks, exit code 0, with zero build warnings/errors
and the 31-assertion probe using the freshly built Core assembly. Saved
[final gate](evidence/038-flow-binding-final-validation.json) and four companion
files. This remains local dirty-checkout evidence, not committed CI proof.

## Remaining full scope

Authored binding capture/stability, complete attachments, versioned durable Flow
source/history/identity, default Runtime/protocol authoring, library gates across
load/rollback/snapshots, required providers/business vocabulary and executable
controlled external-subagent trials remain required. Task-bank shape checks are
not task outcomes; no agent-productivity or native-memory benefit is claimed.
Memory/mailbox/stack-only and conditional LLVM policies remain research/later
work rather than changes to the managed prototype.
