# Flow batch compilation integration

Status: word-batch compilation implemented; focused Flow passed 473 assertions.
Full Release integration and final publication are tracked below. Authored
bindings, complete attachments/storage/default integration remain unfinished.

## Scope and acceptance

Implement `FlowLowering.compileBatchWords` over an existing complete compiler
context plus explicitly identified Flow additions/replacements. Build a
type-distinct signature-only resolution catalog before lowering bodies, preserve
host-assigned stable identities/revisions, and compile the final real dictionary
through the existing semantic IR verifier once. Do not introduce executable
placeholder words, another interpreter, storage writes or Runtime defaults.

Validate complete name/ID coverage, unique nonempty IDs, closed proposed types,
known nominal types/effects, and parameter names/arity before lowering. Preserve
trusted primitive specialization without allowing generic Flow declarations.
Replacement must match identity and expected revision, advance revision and
preserve status/maturity. Reject replacing trusted/generated entries. Reject
malformed input origin maps before pruning replaced-body markers; allocate
disjoint new markers across the batch. Final checking remains authoritative for
effects, scalar validators, retained callers and recursion policy.

Acceptance includes reverse-order forward ordinary/dot/callback calls; vector
versus scalar use; add versus replacement identity/revision; duplicate/invalid
catalogs; disjoint multi-word origins; incompatible retained callers; rejected
undeclared effects and recursive cycles; and unchanged supplied snapshots after
failure. Execute verified results and inspect targets, not merely parsing.

Independent contract review specifically requires synchronized entry/definition
revisions, persistent status/maturity preservation, retained-caller checking
under changed replacement signatures, trusted primitive generic relationships,
generated constructors/accessors and written named-argument evaluation order.
An input snapshot verification before the one final proposed-program compile is
acceptable when that reuses the compiler's authoritative origin checks.

Authored call-binding sidecars and attachments against complete snapshots remain
required follow-on work before durable publication. This first slice must not
claim the complete project-lowering/storage contract is satisfied.
In particular, final verification of retained lowered expressions cannot prove
that unchanged authored dot syntax would still resolve to the same identity.
Re-resolution/collision rejection is deferred to the required binding slice.

A focused follow-on audit found that ordinary/root/dot/generated calls emit
`Expr.Call` with an authored nonzero call span, while static collection callbacks
use the callback-reference span. Zero-width synthetic origin maps and regenerated
IR site ordinals therefore cannot identify authored calls alone. The binding
slice needs exact-source-scoped structural AST paths joined to emitted Expr-tree
locations or an equivalent deterministic verified instruction walk; duplicate
host-built spans must not merge distinct sites. Compare target stable identity
for rebinding, not target revision, while final IR must still use the current
revision. These hooks are planning evidence, not an emitted sidecar.

## Ownership and validation

The Luna/max implementation worker owns FlowLowering, focused Flow tests, any
strictly necessary Core source/fsproj additions, the project-lowering design
document and report 035. Root owns requirements, this integration report,
serial builds, full validation and publication. A separate Luna/max reviewer
audits catalog/replacement/origin/final-verification boundaries read-only. All
share the canonical prototype checkout without worktrees or overlapping edits.

After source freeze run focused Release Flow, IR, interpreter and language
acceptance suites, then:

```powershell
pwsh -NoProfile -File scripts/Validate.ps1 -Configuration Release -ReportPath .agentlang/reports/036-flow-batch-validation.json
```

Record failures and successful final evidence. Update reports in each milestone
commit, push to the private repository, verify committed CI and fast-forward
main with saved evidence. The parent source c0b33dc passed all 24 committed CI
checks; parent publication 84611d5 has separate main run 37378592143 in progress
when this plan is recorded. It subsequently completed successfully for exact
publication commit `84611d5c6de11bffd340a982f3cf5b7036ecc5dd`; saved
[run identity](evidence/036-parent-main-ci.json). This establishes the parent
milestone's post-merge CI result, not a batch-compilation pass.

## Remaining full objective

Required work still includes authored binding stability, durable versioned Flow
source/history/identity, default Runtime/protocol authoring, library gates across
load/rollback/snapshots, missing providers/business vocabulary and executable
controlled Flat/Growing/Conventional external-subagent trials. Existing task-bank
shape checks are not benchmark outcomes. Memory/mailbox/stack-only policies and
conditional LLVM backends remain research/later work. No productivity or memory
benefit is established by this compiler slice.

## First Core build

The fresh Release Core build stopped at compilation with exit code 1,
one warning and nine errors: an indeterminate lookup type, FlowParameter versus
RecordField inference, SourceSpan versus ordered type-list mismatches, and an
undefined rawIdentity helper reference. Saved [terminal evidence](evidence/036-early-core-build-failure.json).
The owner is repairing/refreezing Core before the next serial build. No batch
execution or integration pass is claimed from this attempt.

The repaired fresh Core Release build passed with zero warnings/errors. Core is
frozen for independent source review while the worker adds focused acceptance
fixtures. Successful Core compilation alone is not a batch execution pass.

## Pre-prune validation findings

Root source review found that exact origin-key validation alone could hide an
invalid authored-origin value when replacement removes its marker. The owner
reopened only origin validation and explicit constructor parameter metadata.
Invalid origin spans must fail before pruning, and every supplied parameter
catalog entry must validate even where derived record fields are authoritative.
Focused regressions will replace the word owning the malformed origin and
supply malformed constructor metadata. A fresh build is required after repair;
the earlier Core pass does not cover these changes.

The refrozen Core Release rebuild passed with zero warnings/errors; saved
[terminal evidence](evidence/036-core-review-fix-build.json). Source review and
focused fixture completion continue. No complete gate result is claimed yet.

## Focused existing semantic gates

Fresh Release builds/runs passed IR 102 assertions, interpreter 22 assertions,
and language acceptance 34 groups / 583 assertions against refrozen Core. Saved
the three `036-focused-*-pass.json` terminal reports. The new Flow batch suite
is not yet frozen/run. Independent source review encountered model capacity
before producing a completed report and is being retried; the earlier contract
review is not substituted for frozen source review.

Independent frozen-source review confirmed the repaired origin and parameter
checks, then found an additional host-AST gap: proposed Flow word names were not
validated before the signature overlay, allowing noncanonical/unaddressable names
despite normal parser restrictions. The owner is adding canonical dotted-name
validation and malformed host-AST regressions before refreezing Core. The prior
focused passes remain evidence for their earlier source snapshot, not this fix.

The name-guard Core Release rebuild passed with zero warnings/errors; saved
[terminal evidence](evidence/036-core-name-guard-build.json). The guard validates
dot-separated identifiers before overlay construction, preserving valid hyphen
and question-mark segments. Focused Flow execution is still pending.

## First focused Flow attempt

The first focused Flow run stopped at fixture compilation with exit code 1:
an incomplete binding/indentation around Program.fs lines 2256–2258 produced
FS0010/FS3118, followed by an incomplete construct at line 2308. Saved
[terminal evidence](evidence/036-focused-flow-build-failure.json). Core remains
frozen; only fixtures reopen for repair. No focused execution pass is claimed.

The fixture syntax issue was repaired. Review also required a non-vacuous
constructor-metadata rejection case: valid-but-mismatched supplied names already
demonstrate that derived fields remain authoritative, but do not prove malformed
supplied metadata is rejected. Only tests reopened for that addition before the
next run. Frozen source review completed with no remaining material source
finding after the origin/parameter/name fixes; executed acceptance is still
pending, and the reviewer will check the added fixture.

The second focused attempt also stopped before execution: unannotated new
change records/helpers were inferred as ScalarEntry, so RevisionIntent and
Definition types failed before cascading into batch list mismatches. Saved the
[observed terminal output](evidence/036-focused-flow-type-build-failure.json),
which is explicitly truncated by the output budget. The owner is adding explicit
FlowWordChange annotations throughout the new fixtures. Core remains frozen.

The third attempt compiled and reached execution, then failed a combined
assertion that required both input-snapshot equality and a particular diagnostic
Word value. The retained caller's incompatible signature already produced the
expected TYPE_STACK_MISMATCH; the owner is inspecting diagnostic ownership and
separating the immutability proof from that assumption. Saved
[execution failure](evidence/036-focused-flow-first-execution-failure.json).
Core remains frozen; this is not a focused pass.

After splitting the diagnostic and immutability assertions, the fourth run
advanced to the origin fixture and rejected its stale replacement intent. The
fixture's base word was compiled as candidate revision 0 but requested expected
revision 1. Saved [execution failure](evidence/036-focused-flow-origin-fixture-failure.json).
The owner is correcting fixture revision setup while retaining the separate
stale-revision rejection test. No Core change is indicated by this failure.

## Focused batch execution pass

The fifth focused Release Flow run passed all 473 assertions with exit code 0;
saved [terminal evidence](evidence/036-focused-flow-pass.json). This includes
executed forward ordinary/dot/callback calls, generated operations and named
evaluation order; replacement identity/revision/status/maturity and retained
caller checking; exact origin validation/pruning; and final effect, cycle and
validator rejection. The earlier compilation/execution failures remain saved.

Core and tests are now frozen while docs/report 035 finish and final read-only
fixture review completes. The full fresh 24-check Release gate is the next
validation step; no result is claimed until terminal completion.

## Full local integration

The fresh complete Release gate finished with exit code 0 and all 24 checks
passing. The solution build reported zero warnings/errors. Flow passed 473
assertions, lint 68, IR 102, interpreter 22, language 34 groups / 583 and storage
9 groups / 105. Other harness/business/contracts/value/source/conventional/
discovery/vocabulary checks and fresh-process persistence, matched fixtures,
trial-host, parser limits and both whitespace checks passed.

Saved [full evidence](evidence/036-flow-batch-validation.json) and four adjacent
reports. These identify a dirty prototype checkout based on publication commit
84611d5, not committed CI proof for this source. Task-bank validation is still
artifact-shape evidence (2,114 assertions / 60 tasks / 180 proposed vectors), not
execution of those tasks. No agent-productivity or native-memory gain is claimed.

Final source review found no remaining material issue after the recorded fixes;
the constructor metadata regression was confirmed non-vacuous. Word-batch
compilation is one prerequisite. Authored source-object binding capture and
unchanged-source re-resolution, complete attachment assembly, durable Flow
storage/history, default authoring cutover and controlled trials remain required.
