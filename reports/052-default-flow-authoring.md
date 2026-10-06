# Default Flow authoring cutover

Status: complete local 27-check Release gate passed, 2026-10-06; clean committed-source CI and private publication pending.

Default Flow authoring, interactive source/case editing and matching harness
selection passed complete Release validation. Explicit Stack compatibility and
immutable historical source loading remain. Runtime/project/library gates retain
strong nominal types, declared effects, caller regression and actual branch/site
coverage. [Final local evidence](evidence/052-second-publication-validation.json)
contains all 27 passing checks; source CI is recorded separately below.
## Starting evidence and scope

The previous goal turn made concrete progress: atomic typed Flow documents were
implemented, all 26 local and exact committed-source CI checks passed, and source
plus reports were fast-forwarded to private main at
`3e43e2c291107f1cca269fc0a41f0d7ec888bff6`. The canonical prototype checkout is
clean at this starting revision. Post-publication main CI run 37431535228 remains
active at the first observation; do not substitute it for the already audited
clean source CI run 37430916129.

The full PRD remains open. This milestone completes the requested early Flow
frontend default before further controlled agent trials; it does not implement
LLVM, arenas, mailboxes, real host providers or experimental performance results.

## Inspected gaps and approved plan

A bounded independent review found omitted Runtime authoring selectors still
choose Stack; CLI one-shot evaluation and file definition have no frontend flag;
REPL buffering waits for `end`; and examples/legacy callers need deliberate
frontend migration. Type-source inspection and revision-CAS file updates also
need human command forms. Root inspection confirmed these concrete code paths.

- Runtime: omitted `define`/`eval` select Flow; explicit Stack remains supported
  with no parser fallback. Persisted metadata remains the sole historical loader
  selector. Add incremental single-owner Flow tests/examples through the existing
  retained-source/identity/CAS staging path, preserving caller and library gates.
- CLI: default Flow human/one-shot authoring, explicit Stack selector, parser-driven
  incomplete-input buffering, type-source inspection and replacement revision
  arguments. Protocol requests retain their explicit per-request semantics.
- Compatibility: pin intentional Stack tests/replays explicitly rather than
  weaken their assertions or rewrite historical agent trial evidence.
- Examples: preserve original Stack bytes under `examples/legacy` and migrate the
  primary Customer, refined-type and container examples to Flow with the same
  behavior and test cases. Update current quickstarts with actual syntax.

Flow tests remain owned by user words. They can exercise generated constructors
and accessors; legacy direct generated-owner cases remain available through
explicit Stack. Direct generated-owner Flow metadata needs a separate persistent
ownership design and is not silently implemented with fabricated user histories.

## Ownership and acceptance

Non-overlapping Luna/max implementation agents own Runtime + Flow Runtime tests,
CLI + a new CLI test executable, legacy callers/replay scripts, and the three
examples plus preserved legacy copies. Root owns documentation/reports, solution
and validation integration, serial builds, review and publication. Shared Core
must be frozen for fresh builds; workers do not run builds or commit independently.

Acceptance requires default Flow typed documents/eval, malformed Flow rejection
without Stack fallback, explicit full Stack support and historical reload,
incremental test/example editing with stable identity and actual library coverage,
parser-driven interactive completion and EOF behavior, file revision CAS and type
source inspection, and fresh execution of all migrated examples. Add the CLI
executable to the solution and full release gate. Preserve v1/v2/v3 storage
contracts. Publish only after the focused checks, full local release gate and
exact committed-source CI pass, with reports included.

## Published parent and incremental case contract

The post-publication main CI run 37431535228 completed successfully. The downloaded
artifact confirms exact revision 3e43e2c, a clean checkout and all 26 checks passing;
see [run metadata](evidence/052-parent-main-ci.json) and
[validation](evidence/052-parent-main-validation.json).

The Runtime implementation plan resolves a case-only document to exactly one
existing Flow user-word identity and reuses its exact retained definition. New
case additions capture the current immutable owner revision internally. An
incoming existing case name requires explicit replacement and owner revision CAS;
removals retain the existing expected-source-hash rule. This reuses the established
whole-snapshot validation and persistent caller/test gates rather than adding a
second publication path or a new storage format. Mixed-owner and non-Flow owner
case-only documents fail explicitly. These are accepted implementation rules,
not passing evidence yet.

CLI planning preserves protocol selectors: the process frontend flag affects
human/one-shot evaluation; JSONL and raw request modes reject that flag combination
rather than override request metadata. Interactive Flow buffering retries only
incomplete parser input, with bounded source, and retains explicit Stack end-block
buffering. New CLI acceptance will use fresh processes and bounded timeouts.

## Integration preparation

The three Flow examples and preserved legacy copies are frozen. Independent
static review reports all prior names and 25 tests / 2 examples retained;
original Stack Git blob hashes match the legacy copies. This is source/count
review, not executable evidence. Fresh CLI acceptance will exercise the examples,
including container records' Email dependency, before claiming them runnable.
The new CLI test project is added to the solution and release script; the full
gate will now contain 27 checks. No build has run against the in-progress source.

## Review corrections before execution

Independent README review confirmed Flow syntax and command forms, and caught
an unignored quickstart project path plus imprecise wording about one-segment
names. The quickstart now uses `.agentlang/flow-quickstart`; headers/owners spell
the exact dictionary identity, using dots only for namespace segments.

Root review caught two implementation risks before source freeze: temporary-word
preprocessing must not rewrite a string/comment containing `temp word`, and
case-only edits must not implicitly promote a temporary owner. Owners are adding
regressions and preserving those boundaries. These findings are review evidence;
no current integration test result is claimed.

The harness compatibility plan keeps the six compact agent tool schemas. A
per-run frontend selects authoring, seed input and language primer, while explicit
oracle selectors win. Record that choice in run metadata. Baseline inventory must
read manifest-backed current objects with declared frontends rather than parse a
Flow export with the legacy parser; archived histories remain excluded from
current baseline counts. Historical traces remain unchanged.

The further lifetime review found that the reused legacy replacement path already
preserves a Temporary owner's status; the owner is verifying this with a dedicated
regression rather than changing general lifetime semantics unnecessarily.
A separate full-PRD logging gap is recorded in the requirements ledger:
`wordsCreated` currently conflates revisions/type names with creation. Do not use
that partial aggregate as a validated research count; ordered object-change and
creation metrics remain follow-on work.

## First executable checkpoints

Production Core and CLI compiled in the first focused CLI build. The new CLI
harness then failed four F# syntax errors; after repair the second build exposed
four overloaded-method/type-inference errors. Those were repaired with explicit
helper/process/timeout types, including checking stream-drain timeout results.
The third fresh CLI build passed with zero warnings/errors. Evidence:
[build 1](evidence/052-first-cli-build.json),
[build 2](evidence/052-second-cli-build.json),
[build 3](evidence/052-third-cli-build.json).

The first CLI execution failed after seven assertions because its fixture still
expected omitted JSONL authoring to default to Stack. Approved semantics require
omitted Flow and explicit Stack; the owner is correcting that stale assertion,
not changing production dispatch. See [execution](evidence/052-first-cli-tests.json).

The first and second fresh Flow Runtime builds passed with zero warnings/errors.
The first run reached the new case-only document and found fixture casing
`caseitem.new` instead of the existing generated `caseItem.new` convention. After
that fixture repair, the second run progressed to a new numeric revision assertion
and failed because it requested a String from an Int32 JSON value. The owner is
repairing numeric assertions. Evidence:
[build 1](evidence/052-first-flow-runtime-build.json),
[run 1](evidence/052-first-flow-runtime-tests.json),
[build 2](evidence/052-second-flow-runtime-build.json),
[run 2](evidence/052-second-flow-runtime-tests.json).

All checks used fresh scoped dependencies; no stale binaries were treated as
integration evidence. The independent harness/caller compatibility owner remains
active outside those focused dependency graphs. Neither focused suite nor the
27-check complete solution gate is claimed passing yet.

## Passing default Runtime and incremental cases

After the numeric fixture repair, the third fresh Flow Runtime project build
passed with zero warnings/errors, and the fresh suite passed **18 groups / 578
assertions**. See [build](evidence/052-third-flow-runtime-build.json) and
[execution](evidence/052-third-flow-runtime-tests.json). This includes default
Flow authoring, explicit Stack compatibility, incremental case staging, stable
identity/source/case retention, revision/hash CAS, persistent caller regression,
project/library gates, temporary status/cleanup, frozen-validator rejection and
task abort. Runtime and its acceptance file are frozen for the complete release
gate. The CLI suite and independent compatibility/harness work remain unfinished;
this focused pass does not establish the complete cutover or a research benefit.

## Memory research clarification

The proposed optional mailbox model already appears in the PRD and research
plan: bounded declared retained data outside processing arenas, with all other
language values using an arena-backed program data stack and no independent
language object heap. This remains research, not a V1 allocator change.
Fixed roots must not conceal unbounded heap storage. Retained fields, queue
entries and responses require valid bounded storage before item scratch is
reset; nested scratch references must not survive publication. Compare copying
cost, capacity failures and unused retained capacity alongside cleanup cost.
See [the PRD](../docs/PRD.md), [candidate 4 and acceptance cases](../docs/STACK-ONLY-RESEARCH.md)
and [memory contract](../docs/MEMORY-REGIONS.md). No claim about Stasis internals
or current native memory performance is made.

## CLI integration failure and parser correction

The fourth CLI build found a new fixture indexer syntax error; after its bounded
repair, the fifth fresh build passed with zero warnings/errors. The second CLI
run passed the selector/protocol group and reached 29 assertions, then exposed
a production parser EOF classification issue: an opening word header without
yet-entered effects reports `FLOW_EFFECTS_REQUIRED` instead of incomplete input.
The REPL correctly retries only `FLOW_INCOMPLETE_INPUT`, so subsequent declaration
lines were evaluated separately. The fix belongs in precise parser EOF
classification, preserving rejection of closed/malformed definitions rather
than retrying every error. Focused parser regression coverage and fresh CLI
execution are required before claiming interactive authoring works.
Evidence: [build 4](evidence/052-fourth-cli-build.json),
[build 5](evidence/052-fifth-cli-build.json),
[run 2](evidence/052-second-cli-tests.json).

The precise EOF parser correction is now frozen with `parseWord` and
`parseDocument` regression assertions, preserving closed/malformed metadata
errors. The sixth fresh CLI build passed. The third CLI run demonstrated valid
multiline staging and subsequent case execution, then failed a stale fixture
substring expecting the old Stack `defined` wording; Flow correctly reports
`validated and staged`. The fixture owner is auditing response-text assertions.
See [build 6](evidence/052-sixth-cli-build.json) and
[run 3](evidence/052-third-cli-tests.json). Full CLI/gate completion remains
unproven until fresh execution passes.

The focused Flow build and execution passed **981 assertions**, including exact
EOF-versus-malformed metadata classification through both parser entry points.
See [build](evidence/052-first-flow-build.json) and
[execution](evidence/052-first-flow-tests.json).

Compatibility implementation is frozen for compile. The first fresh Harness
build found an unresolved `SourceFrontend` type import in `Harness.fs`; the
owner is making a bounded import correction. No Harness execution or integrated
gate is claimed passing from that failed build. See
[evidence](evidence/052-first-harness-build.json). Historical replay fixtures
retain their original sources and now select Stack explicitly at execution.

The seventh fresh CLI build passed; the fourth run passed four groups and 55
assertions (selectors, interactive buffering, malformed/EOF recovery, and file
CAS/stable source identity). It then found a fixture assuming a leading project
comment belonged to a type source member. The existing atomic document contract
retains exact declaration member bytes, so the fixture is moving the comment
inside the type declaration rather than changing source/persistence semantics.
See [build 7](evidence/052-seventh-cli-build.json) and
[run 4](evidence/052-fourth-cli-tests.json).

The second Harness build compiled production Benchmarks successfully, then found
three fixture indexer syntax errors. Those assertions are being repaired; see
[evidence](evidence/052-second-harness-build.json). These integration repairs
are development findings, not measured external-agent error-recovery results.

## Passing process-level CLI acceptance

The final fresh focused CLI build passed with zero warnings/errors, followed by
**7 groups / 80 assertions** passing. This covers default Flow and explicit Stack
protocol selection, parser-driven interactive declarations/cases, incomplete EOF
and malformed recovery, file definition and revision CAS, stable identity/history,
exact type member source and reload, all migrated examples, library commits, and
legacy end-terminated REPL compatibility. The three Flow examples passed all
**25 tests**, with their two examples and fresh-process vocabulary execution.
See [build](evidence/052-ninth-cli-build.json) and
[execution](evidence/052-sixth-cli-tests.json). Earlier exact-source fixture and
Stack response-text repairs are retained in the evidence files.

The fourth Harness build passed with zero warnings/errors. Its first execution
found a setup replacement retaining an old failing test. Existing commit gating
correctly rejected that proposal; the fixture is refreshing the retained case
instead of weakening production validation. See
[build](evidence/052-fourth-harness-build.json) and
[execution](evidence/052-first-harness-tests.json). The current-head inventory
fixture must not claim historical-only case removal when the old case remains
in the current revision.

Independent static review found no actionable regression in case-only Runtime
editing, stable IDs/status/source, caller checks or actual library coverage.
Frozen validator closure attachment editing remains intentionally rejected; this
is a policy limitation rather than a demonstrated safe metadata-edit path.

## Inventory identity correction

The fifth fresh Harness build passed, but the second execution exposed an actual
inventory contract mismatch: Stack parser test/example `.Name` values are case
names, while the new Flow inventory used `word/case`. The mixed-current-head
assertion correctly failed. The fix requires a consistent identity encoding and
an audit of retained benchmark lineage origins, not a weakened test. Older
experiment origin records must retain their original representation and provenance;
new reports need an explicit encoding indicator before fully qualified names
are used. See [build](evidence/052-fifth-harness-build.json) and
[execution](evidence/052-second-harness-tests.json). Complete gate remains pending.

The fresh general language acceptance build passed with zero warnings/errors,
followed by **34 groups / 583 assertions** passing. Historical Stack interactions
are explicit; task/snapshot/rename/container/library behavior remains covered.
See [build](evidence/052-first-acceptance-build.json) and
[execution](evidence/052-first-acceptance-tests.json).

Inventory correction plan: new inventories use qualified test/example identities
and `attachmentNameFormat: "word-case/1"`; manifest Stack attachments must match
their owner head. Untagged lineage-v2 origin inventories retain legacy case names
verbatim. Unknown tags and malformed qualified names fail validation. These are
under implementation/test, not yet a passing contract. The first slash separates
owner and case; valid legacy cases containing additional slashes are preserved.

The inventory implementation and new migration assertions were written before
the Luna/max worker terminated with a model-capacity error. Authoritative source
inspection confirmed the new format tag, qualified names, owner checks and
legacy-origin tests were present. The sixth fresh Harness build compiled
production code, then found a new fixture chain-parenthesis error. A bounded
fallback worker is repairing/auditing only Runner and Harness tests; no stale
binary or unfinished worker report is treated as passing evidence. See
[build](evidence/052-sixth-harness-build.json).

The eighth fresh Harness build passed with zero warnings/errors. The third run
passed through the new qualified inventory and untagged legacy-origin checks,
then failed a later historical fixture's direct RPN `define` request, which had
not selected Stack. The remaining fixture pin is being corrected; old origin
preservation is also being asserted across the whole semantic JSON object.
See [build](evidence/052-eighth-harness-build.json) and
[execution](evidence/052-third-harness-tests.json). The fixture syntax failure
from the seventh build is retained in its evidence. No complete Harness pass is
claimed yet.

## Harness pass and first complete gate

The ninth fresh Harness build passed with zero warnings/errors, followed by
**220 assertions** passing. New inventory encoding, qualified identity/owner
checks, mixed current heads, explicit frontend overrides, invalid format rejection
and whole-object legacy origin preservation are covered. See
[build](evidence/052-ninth-harness-build.json) and
[execution](evidence/052-fourth-harness-tests.json).

The first complete Release gate finished all **27 checks: 25 passed, 2 failed**.
The build had zero warnings/errors. Failures were historical RPN fixtures in
Source.Tests helper requests and a Storage.Tests direct request omitting explicit
Stack. All other suites and fresh process verifiers passed, including 70 matched
fixture checks, 31 Flow call-binding assertions, parser limits and the bounded
subagent host. This is a failed gate, not publication approval. See
[full evidence](evidence/052-publication-validation.json). The two fixture pins
are being corrected and the exact CI command will be rerun in full.

## Complete local release acceptance

After explicit Stack pins in the Source/Storage historical fixtures, the exact
CI command was rerun. The complete **27-check Release gate passed**, with zero
build warnings/errors. See [final local evidence](evidence/052-second-publication-validation.json)
and its process-verifier sidecars. This is a dirty-tree test of the source based
on parent `3e43e2c`; it is not clean committed-source CI.

Default Runtime/protocol, human REPL and one-shot authoring now choose Flow.
Explicit Stack and source-format-selected historical loading remain. The CLI,
incremental single-owner case editing, typed sources, migrated examples and
matching harness frontend/inventory contracts passed focused and complete gates.
The three preserved legacy examples match their original parent Git blobs.
A documentation replacement mistake affected README capital letters only; it
was corrected and the complete README/link targets reviewed before staging.

Private repository publication and exact source CI remain next. This milestone
does not complete real confined host providers, full business-language fixtures,
complete task/usage/reuse metrics, all named introspection commands, or controlled
Flat/Growing/Conventional trials. No native allocator, mailbox, LLVM backend or
measured agent benefit is claimed. The complete PRD goal remains active.
