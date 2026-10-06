# Flow attachment source-binding integration

Status: local integrated acceptance passed; publication in progress. The full PRD remains active.

## Authoritative baseline

The canonical checkout is `D:\code\AgentLang`, branch `prototype`, at
`469d2f5cba6e2843c4eabd36e0e0a692e9a4396f` before this slice. It is clean before
the new evidence/report files. No worktree is used. Source commit `cff4a2d`
passed exact clean committed CI with 25 checks; its reports-only publication
was fast-forwarded to private main/prototype. Publication main CI also passed:
[run](https://github.com/benwmaddox/AgentLang/actions/runs/37392678598),
[saved identity](evidence/039-parent-main-ci.json).

The preceding goal turn made progress: verified source-call bindings, new tests,
saved reports and CI evidence, and private main integration. The current slice
advances source-backed tests/examples toward complete durable Flow authoring;
it does not substitute a compiler-only endpoint for the remaining PRD.

## Plan and trust boundary

A bounded read-only Luna/max planning pass inspected Flow attachment lowering,
the source-backed word batch, detached compiler APIs, and IR ownership rules.
Its early checkout observation occurred before root's final publication; the
baseline above supersedes that observation. The plan is implemented by a
Luna/max worker with exclusive ownership of FlowLowering, optional FlowSyntax
changes, focused Flow tests and the project-lowering document. Root owns this
report, the requirements ledger, validation evidence and publication.

The compiler context has no test/example catalog. The host therefore supplies
an exact attachment inventory keyed by stable owner ID, case kind and case name,
with selected source references and document bytes. The compiler verifies strict
UTF-8 hashing, object kind, parsed ownership/case, revision and source labels;
the host remains responsible for manifest membership and the completeness of
the asserted inventory. No parser fallback or inferred attachments are allowed.

Explicit add/replace/remove operations control changes. Replacement/removal
compare the selected old source reference; duplicate operations and incomplete
inventories fail. Unchanged source is recompiled against the final proposed
dictionary and cannot silently redirect to a different stable target identity.
Same-ID target revision advancement is allowed and reconciled with final IR.
Owner replacement retains its cases and updates owner revision metadata rather
than silently discarding attachments.

Test actual bodies, pure expected-expression bodies, and example actual bodies
have separate binding identities. Each uses detached `VerifiedIrBody` with
`SiteOwner=None`; it is not a word function. Reconciliation checks exact call
count/order, structural paths, spans, source kinds, target ID/revision/name and
operation kind. Actual static callbacks/generated calls count; implicit scalar
validator calls are excluded as before. Expected code remains isolated from
tested-word coverage.

One allocation seed spans attachment lowering, but each detached compiler call
receives only exact program-context origins plus that case's private markers.
Every case compiles against the same final verified word program. Existing
standalone APIs remain compatible; Stack-authored attachments remain a host
integration responsibility. Storage v2, Runtime dispatch, publication gates,
rollback, snapshots and default Flow cutover follow this compiler slice.

## Acceptance and validation

Require substantive checks for missing/extra/duplicate inventories; malformed
source kind/hash/UTF-16, ownership/case/revision; add/replace/remove with stale
references; literal/error/expression expectations and examples; actual/expected
binding separation; shared marker allocation; retained owner and target revision
advancement; genuine stable-ID rebinding and ambiguity; exact snapshot checking;
type/effect/purity checks; and library own-site coverage isolation. Preserve v1
fixtures unchanged. Compiler success does not prove test execution or library
publication acceptance.

Focused commands, serial after source freeze:

```powershell
dotnet build src/AgentLang.Core/AgentLang.Core.fsproj -c Release --no-incremental
dotnet run --project tests/AgentLang.Flow.Tests -c Release
dotnet run --project tests/AgentLang.IR.Tests -c Release
dotnet run --project tests/AgentLang.IR.Interpreter.Tests -c Release
```

Full gate:

```powershell
pwsh -NoProfile -File scripts/Validate.ps1 -Configuration Release -ReportPath .agentlang/reports/039-flow-attachment-validation.json
```

No new build or test pass is claimed yet. Root will preserve failed attempts,
review final changes independently, run the full gate, then commit source with
updated reports, push privately, audit exact committed CI and integrate main.

## First Core checkpoint

The new public attachment contract, source validation, explicit mutations,
base/final detached compilation and retained binding checks are implemented.
The worker reported a normal Release Core build passing in 19.33 seconds with
zero warnings/errors. That is a reported compile checkpoint; its full terminal
transcript was not saved. A repeated up-to-date build is not new proof.

Root then froze Core and ran the explicit fresh no-incremental command above.
It terminated with exit code 0, zero warnings/errors, in 17.71 seconds:
[exact split terminal evidence](evidence/039-attachment-core-fresh-build.json).
No old-assembly substitution was used. New acceptance fixtures are still being
added, so this is not yet a pass for the attachment semantics. The final focused
suite includes the existing 564 assertions; root deliberately did not build that
project while its source was changing. A read-only independent contract review
is in progress against the frozen Core implementation.

## Independent contract finding

Review found a material owner-format gap after the first Core checkpoint:
attachment validation checked user-word existence, ID and revision, but accepted
a well-hashed Flow case for a Stack-owned user word. Durable cases inherit their
owner revision's frontend, so user-word existence alone is insufficient.

The source owner is adding proven base Flow owner membership and final membership
in the base Flow set plus explicit Flow word changes. The acceptance owner will
exercise a valid-hash Stack-owner rejection and a same-ID Stack-to-Flow revision
with a new Flow case. Existing standalone lowering APIs remain compatibility
APIs; the new complete source-backed proposal has the stronger ownership boundary.
Review found no other material issue in the inspected reconciliation, origin,
CAS, revision or expected-body isolation paths. Final frozen verification remains
required after the fix and fixtures; the first Core checkpoint predates this fix.

The source owner subsequently froze Core including the frontend guard,
dedicated owner-ID diagnostic, body-role/path rebinding details and host-AST
same-span binding helper. Root's second fresh no-incremental Core build passed
with exit code 0, zero warnings/errors, in 17.59 seconds:
[full output and source hash](evidence/039-attachment-final-core-build.json).
Independent final source review and acceptance fixtures remain in progress.

Final source review confirmed the frontend gap is closed. Base cases require
the declared proven base Flow owner set; final cases require that set plus
validated Flow additions/replacements. Empty word changes still validate the
base word source inventory. Actual/expected role sets, structural path sets and
stable target IDs are compared for retained source; target revision advancement
remains permitted. Owner metadata advances while source references stay fixed,
and mutations retain their old-reference checks. No further material source
finding was reported. Final acceptance review and execution remain required.

During acceptance construction, the test owner found an inconsistent blank-ID
diagnostic: key validation rejected the same invalid ID with a generic key code
before document validation's dedicated owner-ID code. Root chose one dedicated
`FLOW_ATTACHMENT_OWNER_ID_INVALID` outcome across both paths; invalid case names
retain `FLOW_ATTACHMENT_KEY_INVALID`. The source owner applied this small change
and froze Core again. It postdates the 17.59-second build and must be included in
the next fresh build/focused/full gate. No earlier checkpoint is relabeled final.

## External client smoke

Root rebuilt latest frozen Core with `--no-incremental`: exit code 0, zero
warnings/errors, 17.18 seconds; saved [build](evidence/039-attachment-final-core-build-2.json).
A separate reproducible FSI client uses the public source-backed project API,
independently checks actual/expected/example values 5/5/10, keeps test/example
keys distinct despite a shared case name, then advances the owner under its same
ID and verifies both cases/source refs remain with exact callee revision 2.
It passed 19 checks against that fresh assembly:
[script](evidence/039-source-attachment-smoke.fsx),
[output and assembly hash](evidence/039-source-attachment-smoke-pass.json).

The first smoke attempt had an F# record-lambda separator error in root's host
fixture ([compile failure](evidence/039-smoke-fixture-compile-failure.json)).
The next used `value(...)` instead of the existing `=> value expression`
expectation syntax ([parse failure](evidence/039-smoke-fixture-parse-failure.json)).
Root corrected both fixtures without changing Core or the parser. These are
preserved failures, not language or agent-benchmark outcomes. The smoke checks
exercise the supported client lifecycle; they do not replace complete guard,
coverage, full-gate or committed-CI acceptance.

## Expanded focused attempts

The first two runs rejected multiline fixture binding layout before executing
tests: [first](evidence/039-first-focused-flow-build-failure.json),
[second](evidence/039-second-focused-flow-build-failure.json). The first repair
addressed a nearby tuple list; the next output pinpointed the remaining migration
RHS. Moving that RHS to an indented continuation resolved the parse failures.
The third exposed F# record inference in a table of document mutations; an
explicit source-document function type resolved it:
[third](evidence/039-third-focused-flow-build-failure.json).

The fourth reached source validation and rejected new-owner cases placed both
in the base inventory and in Add intents. The fixture now supplies an empty
base inventory and explicit additions: [fourth](evidence/039-fourth-focused-flow-execution-failure.json).
The fifth caught an incorrect equality assertion between a statement-body path
and an expected-expression root; the fixture now checks their correct separate
paths and body-role keys: [fifth](evidence/039-fifth-focused-flow-execution-failure.json).
The sixth caught an assumption that every case needs private markers. Literal-only
cases correctly need none; the assertion now checks non-vacuous allocation across
compound cases and exact disjointness: [sixth](evidence/039-sixth-focused-flow-execution-failure.json).
These are fixture corrections without changes to compiler semantics.

The seventh progressed through the preceding behavioral and guard cases, then
found a real source diagnostic gap: ambiguity preserved its code and span but
had `Word=None`. The requirement is a useful caller diagnostic, so the assertion
is retained and the source owner is adding source-backed attachment ownership,
case/body-role/path context: [seventh](evidence/039-seventh-focused-flow-execution-failure.json).
The fix must preserve original error code/span/details and work for actual,
expected and example bodies in both base and final compilation. Latest Core
build and smoke evidence predate this new fix; fresh validation remains required.

The diagnostic enrichment was then freshly built with `--no-incremental`: zero
warnings/errors, 16.33 seconds, with source hash and full output preserved in
[fresh build](evidence/039-attachment-diagnostics-fresh-build.json). The external
client passed all 19 checks against that rebuilt assembly, including its loaded
assembly hash: [client smoke](evidence/039-diagnostics-client-smoke.json).

Attempt eight passed the independently scoped actual/expected/example ambiguity
checks, then reached a fixture with no change intent. The API correctly rejected
that empty proposal before frontend ownership validation. The negative fixture
now supplies an explicit Add with empty base attachment inventory; the positive
Stack-to-Flow migration remains unchanged. The failed attempt is preserved in
[eighth run](evidence/039-eighth-focused-flow.json). Attempt nine passed all
705 Flow assertions: [focused acceptance](evidence/039-ninth-focused-flow.json).
The full 25-check Release gate passed on this exact source and fixture checkpoint:
[complete local evidence](evidence/039-flow-attachment-validation.json), with
zero exit codes for every check and zero build warnings/errors. Companion
projection, matched-fixture, parser-limit and subagent-host records are preserved
beside it. These reuse existing deterministic checks; no new live agent experiment
or productivity/memory result is claimed. The working tree was dirty and its
recorded baseline revision is not a clean committed-CI result.

Final read-only Luna/max review found no material source or fixture defect. It
verified base/final diagnostic wrapping, preserved diagnostic fields, distinct
actual/expected/example attribution, non-vacuous Stack-owner checks and separate
keys for identical-span host AST bodies. The independent client also checks
example source preservation and revision advancement. The code and fixtures
remain frozen for committed CI; reports are updated before publication.

## Next storage audit

A separate read-only Luna/max audit inspected Storage and its frozen v1 tests,
without reading the worker's changing attachment code. It confirmed the three
word-revision constructor boundaries (parser, Runtime manifest builder and test
fixture), the shared current version constant, and the unchanged v1 pointer/
snapshot envelopes. Storage-owned DTOs must use source references, typed tags
and bounded structural paths without depending on later compiler modules.

One proposed field was rejected during root review: durable bindings must not
persist target revisions. They retain stable target identity; final compilation
checks the selected current revision. Otherwise allowed same-ID callee updates
would make immutable unchanged-caller metadata stale. The existing durable plan
already requires this distinction. Binding order, source/case/body-role/path
uniqueness, revision source membership and explicit limits remain schema checks;
parsed-source and verified-IR correspondence remain Runtime/compiler checks.

The audit's suggestion to persist emitted order also needs separation from the
existing durable contract: compiler events use IR traversal order, while durable
serialization sorts source-reference/site keys canonically. Stored list position
is never call identity or execution order. The durable plan now states this
handoff and attachment body-role scoping explicitly; no schema bytes have changed.

The later storage acceptance will preserve all six literal v1 hashes, migrate
history under the same word ID, round-trip deterministic v2 metadata, reject
unsupported/malformed versions before object dispatch and restore a v2 manifest
through a v1 snapshot. No storage implementation is claimed in this slice.
