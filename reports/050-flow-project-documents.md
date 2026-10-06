# Flow project documents and typed source persistence

Status: implementation in progress, 2026-10-06. The full PRD is still active;
this milestone does not mark default Flow cutover or measured experiments complete.

## Verified starting state

Canonical checkout `D:\code\AgentLang`, branch `prototype`, started clean at
`e61591809fa308f943baa114473d2ad4a21f5c4f`, shared with local/remote main.
[Publication CI 37425519500](https://github.com/benwmaddox/AgentLang/actions/runs/37425519500)
passed; the downloaded artifact proves that exact clean revision and all 26
checks. Saved [run identity](evidence/050-parent-main-ci.json) and
[validation artifact](evidence/050-parent-main-validation.json) are audited.
The previous goal turn made progress: maintenance implementation, local and exact
CI validation, reports and private main integration. No goal-completion claim.

## Approved architecture

Flow project documents contain records, nominal/refined scalar types, words,
tests and examples. Brace-based declarations use named fields and explicit
validator addressing:

```text
record Customer { field email: Email; field balance: Float; }
type Email : String { validate email::valid; }
type Meters : Float {}
```

Fields end with semicolons; validators may be absolute root `::name` or qualified
`namespace::name`, never ambiguous short/dotted expression spellings. The parsed
scalar validator retains its exact dictionary key. Named types remain nominal;
base wrappers are initially Int, Float or String, and pure scalar-to-Bool
validators retain existing runtime checks and freeze rules. Document parsing
retains member source bytes and global spans, enforces closed type/name grammar,
and does not invoke a Stack fallback. Existing single-object parser entry points
still reject trailing objects.

Staging builds one proposed dictionary of all types, generated type words,
authored words and attachments. Type checks, effect checks, validator closures
and complete binding validation precede activation. Sequential calls to existing
activating registration methods cannot provide this atomicity.

Manifest v3 is required when Flow types first persist and is retained thereafter.
V1/v2 encodings and immutable source hashes remain unchanged. V3 type entries
carry source format and an optional validator stable target; pointer/snapshot
envelopes stay version 1. Runtime preserves authored type bytes, reloads through
the declared parser, verifies validator identities, and emits deterministic
frontend-marked project source. Types are immutable; source inspection returns
their actual creation source, not fabricated word revisions.

## Ownership and acceptance

Pure syntax/parser/renderer and pure Flow test ownership are separate. Storage
owns v3 DTO/version branches plus Storage tests. Runtime owns atomic document
staging and type-source lifecycle integration after its bounded design review.
Root owns reports, architectural review, fresh builds and publication. All agents
share the canonical checkout and preserve others' changes; no worktrees.

Acceptance: one Flow file can define a record, validated nominal type, validator
and dependent words with cases; a bad later member leaves no partial staging.
Nominal mismatches and impure/wrong-signature validators fail. Commit/reload/source,
rename, task abort and snapshots preserve type bytes, stable validator targets
and word bindings. Mixed historical Stack and new Flow sources remain explicit.
V1/v2 golden hashes remain stable; v3 corruption and unsupported versions fail
structurally. No interpreter fallback or implicit base-type coercion is added.

Root serializes fresh Release builds. Focused Flow/Storage/Runtime/Source/Acceptance
checks follow changed source. The full `pwsh -NoProfile -File scripts/Validate.ps1`
gate and versioned projection oracles run before source publication; exact clean
source CI precedes main integration with reports updated. Default CLI/REPL,
examples and experiment fixture cutover follow complete authoring conformance.

## Integration decisions

The existing `define` operation is extended; no new agent protocol operation is
introduced. Single-word replacement/CAS/attachment requests retain their adapter
and exact full input source. New project documents are add-only and stage every
member atomically. Later word/type commits retain selected dependency closure
semantics, rather than making a source document an indivisible batch.

Flow types retain exact authored declaration object bytes. Canonical export is
a separate projection checked on reload. Document parser members retain global
spans for authoring diagnostics; Runtime reparses immutable member objects under
hash-derived file labels before compiling/persisting their local source bounds.
Temporary project type declarations are explicitly rejected until types have a
temporary lifecycle. Cases attached to new Flow words use the established
binding/coverage machinery. Broader case ownership and default tooling remain
part of complete authoring/cutover acceptance, not hidden interpreter fallbacks.

## First foundation validation

The first fresh solution Release build stopped in the new document parser
with three errors and one warning: the duplicate-type helper's span parameter
was inferred as the private token record. [Build diagnostics](evidence/050-first-solution-build.json)
are saved. The parser owner is adding an explicit `SourceSpan` annotation before
the next fresh build. Runtime remains frozen at the compatibility-only v3 DTO
shape update; full atomic document/type-source integration has not landed.
No executable document, storage suite or full milestone pass is inferred.

The second fresh solution build compiled Core's new parser/storage code and
Runtime compatibility update without warnings, but stopped in four test-fixture
errors: a Flow helper needed an explicit string argument for `IndexOf`, and three
Storage method-call arguments needed parentheses. [Build evidence](evidence/050-second-solution-build.json)
is saved. The respective owners are repairing those fixture compile errors;
no focused execution result is available yet.

## Verified foundation checkpoint

The third fresh solution Release build passed with zero warnings/errors;
[build evidence](evidence/050-third-solution-build.json) is saved. The frozen
foundation passed [Flow](evidence/050-first-flow-tests.json) (964 assertions),
[Storage](evidence/050-first-storage-tests.json) (15 groups / 305 assertions),
[Flow Runtime](evidence/050-first-flow-runtime-tests.json) (15 groups / 399 assertions),
and [language Acceptance](evidence/050-first-acceptance-tests.json)
(34 groups / 583 assertions). These results include the parser and v3 storage
contract changes with Runtime's legacy DTO compatibility fields only; they do
not claim the new Flow typed document protocol/lifecycle is implemented.

Runtime integration is now released in its separate owned file, along with
independent public Runtime acceptance in report 051. Source will change again;
that integration requires a fresh solution build, focused lifecycle acceptance
and full versioned publication gate before committing/pushing this milestone.
The complete PRD, default frontend cutover and controlled agent evaluation
remain open.

## Independent review after foundation

Storage v3 received a separate read-only review of required type metadata,
v1/v2 encoding preservation, shared v2/v3 word revision shape, target IDs,
source-role/ref/hash collection and versioned pointers. No concrete new Storage
defect was found; this is static review, not extra execution evidence.

Parser/renderer review found one actionable public host-AST boundary: record
field validation was iterative but lacked a depth bound before recursive type
formatting; unsupported scalar-base diagnostics could also format an arbitrarily
deep type. The source owner is adding a structured nesting-limit guard and the
independent test owner is adding bounded regressions. The previous 964-assertion
Flow pass predates this repair and must not be used to claim it passes.
Runtime staging/type-source lifecycle integration remains active, not frozen.

## Runtime type-source compilation checkpoint

The fourth fresh solution build compiled Core's type-source state, v3
write/load/export checkpoint and renderer depth repair, plus the Flow test
assembly. It stopped at one new Runtime fixture method-call syntax error
(`FS0597` at Program.fs:1951); see [build evidence](evidence/050-fourth-solution-build.json).
The newly built [Flow suite](evidence/050-second-flow-tests.json) passed 971
assertions, including the structured host-type nesting guard. The Runtime
fixture owner is correcting the compile error before the next build. This
checkpoint compiles the type-source lifecycle path but does not execute new
project-document staging, whose builder/dispatch is still being implemented.

The fifth fresh solution Release build passed with zero warnings/errors
([build evidence](evidence/050-fifth-solution-build.json)). The new typed-document
acceptance fixture now compiles. The next [Runtime execution](evidence/050-second-flow-runtime-tests.json)
reached that fixture and failed its initial define with `FLOW_EXPECTED_TOKEN`:
`Expected 'word', found 'record'`. This confirms the remaining public dispatch
still invokes the one-word route; it is not a passing document/lifecycle result.
Runtime is released to finish the shared atomic staging builder, define routing
and type source inspection. The fixture remains strict and will rerun against a
fresh full integration build. No commit/push for this milestone yet.

## Runtime fixture review before integration execution

Independent review of the new public fixture found checks that would otherwise
reject too early or assert the wrong semantics. Compact one-line words put bodies
on the required effects metadata line; those are being made multiline so negative
cases reach type/validator validation. The Customer field is nominal Email and
must explicitly unwrap before invoking a String validator. Invalid Email
construction must expect `REFINEMENT_FAILED`, rather than evaluate the same
failing expression as an expected value. Float payload inspection uses the
existing invariant `G` display (`1` for `1.0`). A scalar constructor's incompatible
ordinary argument uses its call type diagnostic, not a container payload code.
The fixture owner is correcting these cases and replacing the overly broad
purity-error assertion with the actual validator-effect diagnostic. Runtime
source remains in progress; no new build/run result is claimed.

## Pre-integration review and specification alignment

The independent read-only review found no concrete defect in saved Runtime
type-source read/write/export helpers. This is review evidence, not a runtime
acceptance pass. The fixture owner restored both the invalid Customer
construction case and a post-reload direct evaluation expecting
`REFINEMENT_FAILED`; the Customer word retains two attached tests.

The PRD now shows target Flow scalar declarations and explicitly requires atomic
whole-document staging, selected dependency-closure commits, exact authored type
source and preserved validator identities. It links the implementation gates
without presenting the current work as completed or default Flow as shipped.

## Atomic staging integration build

The Runtime owner froze the completed atomic builder, document dispatch and
type-source inspection path. The sixth fresh Release solution build then failed
with six `FS0001` errors in the builder: overlapping RecordEntry/ScalarEntry
field labels caused F# to infer record helpers/maps as scalar entries. See
[build evidence](evidence/050-sixth-solution-build.json). The owner is adding
explicit entry/map annotations before a fresh rebuild. No tests were run from
stale assemblies, and the public document acceptance gate remains unproven.

The bounded inference repair separates record/scalar entry maps and projects
common type-source pairs before concatenation. The seventh fresh Release solution
build passed with zero warnings/errors ([evidence](evidence/050-seventh-solution-build.json)).
The fresh Flow Runtime suite then passed **16 groups / 491 assertions**
([evidence](evidence/050-third-flow-runtime-tests.json)), including the new
whole-document typed source, selected commit, refined-value rejection, exact
source/hash/identity, reload/CLI and rollback/snapshot cases. The full release
gate is running. README, storage notes and requirement status now describe the
opt-in document interface; default Flow and research benefits remain unclaimed.

## Full local release gate

The fresh `scripts/Validate.ps1` release gate passed all **26 checks**
([validation](evidence/050-publication-validation.json)). Build reported zero
warnings/errors. Flow passed 971 assertions; Flow Runtime passed 16 groups / 491
assertions; Storage passed 15 groups / 305 assertions; language acceptance passed
34 groups / 583 assertions. Persistence projection, matched fixtures, prior
subagent host verification, parser limits and both diff checks also passed.
These are implementation/compatibility checks, not new agent experiments.

The report identifies parent revision e615918 and a dirty checkout truthfully;
committed-source CI must still validate the publication source commit. GitHub
repository privacy was reconfirmed before publication. Default frontend cutover,
generated-owner attachment authoring and controlled agent evaluation remain
open. This milestone does not implement arenas, mailbox execution or LLVM.
