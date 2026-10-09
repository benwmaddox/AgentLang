# Test-file scoped dependency replacements

Status: focused validation, the documented example and all 37 full local Release
checks pass. This is product engineering, not an efficacy trial.

## Behavior

Flow/2 test files can contain `override fn` declarations shared by nested tests.
A replacement reaches direct calls, transitive callers and static list callbacks.
It preserves the original input/output types and uses a compatible subset of
its effects. The original capability checks remain mandatory, even for a pure
fixture. Normal execution continues to use the original definitions and the
real project-rooted filesystem introduced in report 161 by default. Explicit
virtual mode still supports whole-session simulation.

Each actual test and executable expectation uses fresh virtual filesystem state.
Replacement dispatch is keyed by resolved identity and revision and exists only
in an ephemeral verified program. Production IDs, bodies and history are
preserved. Passing cases, assertion failures and runtime errors do not leave
replacement dispatch installed. Active overrides appear separately in results.

A fixture cannot supply own-body, branch, finite-input or finite-return coverage
for the original function. A caller can qualify its real body against mocked
dependencies, but authored library dependencies still need their own real-body
qualification. Generated operations and polymorphic primitives are rejected by
this initial replacement mechanism.

## Durable source boundary

Manifest v4 retains v3 fields and adds shared complete test-file sources plus
explicit override-header/body binding roles and paths. One wrapper is stored
once even when it contains several cases. Reload selects cases by owner/name,
rechecks signatures/effects and compares retained binding identities. Rename and
case removal rewrite complete wrappers. V1-v3 codecs and older fixtures remain
supported; older manifests reject the new binding forms rather than misread them.

A wrapper has one tested owner and at least one case. Owner/label pairs and
owner/case keys must be unique across standalone tests and wrappers. Different
owners may reuse a case name. New project documents may define an owner with its
wrapper; attaching to an existing Flow owner uses the test-file-only operation.
Mixed project transactions cannot silently attach a wrapper to an owner outside
that transaction's definitions.

## Focused validation

- Fresh Core Release rebuild: zero warnings/errors.
- IR interpreter: 121 assertions.
- Flow parser/lowering/source paths: 1,262 assertions.
- Canonical source: 108 assertions.
- Storage: 16 groups / 446 assertions.
- Integrated Flow runtime: 36 groups / 1,329 assertions.
- CLI: 12 groups / 188 assertions.
- The documented example passes ordinary non-test real reads/writes, fixture-only
  tests, library publication and fresh-session reload. Real function bodies run
  against virtual state during tests, leaving host files unchanged.
- Full Release validation: all 37 checks pass, including business-policy
  preflight's 98 checks across 30 controlled outcomes. The gate ran from
  14:21:59 to 14:58:43 EDT on 2026-10-09 with serial builds and package auditing
  skipped. Fresh focused builds have zero
  warnings/errors with package auditing explicitly disabled for offline checks.

Focused failed attempts are retained: Core record inference/result decoding,
IR test syntax and source-origin helpers, isolated-build configuration, and
an authoring-help indentation error. Later fixture corrections addressed missing
pre-commit tests, invalid primitive spelling, inline effects metadata, exact
binding/case inventories and the protocol's quoted string representation.
A CLI attempt with a temporary project outside the writable workspace failed
with access denied; workspace-local temporary projects pass. None of these
failed attempts is reported as a passing check.

The independent review requested a header-only binding-tamper regression.
It exposed a real implementation defect: wrapper compilation refreshed saved
bindings before comparing them, so it compared regenerated proofs to themselves.
The fix preserves saved proofs until all comparisons pass, then refreshes them.
The same negative case now rejects the forged identity with
`FLOW_RUNTIME_BINDING_MISMATCH`. A second regression verifies exact manifest,
wrapper reference and source restoration after committing a task-local change
and aborting the task. V4 generated-type cases also survive aggregate reload.

## Feedback

The useful correctness boundary here is richer than type checking: the forged
binding was well-typed and structurally valid, and static review missed the
comparison-order bug. A negative behavior test exposed it. Scoped fixtures now
make dependency behavior controllable without letting a substitute manufacture
library qualification. This supports the testing approach, but is not evidence
that an agent will choose strong assertions or reuse abstractions successfully.

## Scope and limits

This is product engineering, not a new agent-efficacy experiment. It does not
change report 159's comparison outcome or establish JIT invalidation. Source
fixtures currently execute through the managed interpreter; future JIT callers
must invalidate the affected closure or use interpreted test dispatch. Explicit
structured task-log override entries remain outstanding; active replacements are
currently reported in test-result JSON. Dictionary
metadata persistence remains real host tooling, separate from language I/O in
tests. The real filesystem provider's trusted-root and race limitations remain
as documented in report 161.

## Durable evidence

[The evidence archive](evidence/162-test-file-dependency-overlays/evidence.zip)
contains the full validation report and log, focused passing and failed attempts,
the documented CLI example requests/responses and project metadata, review notes,
and the frozen Git-index source files. [Archive metadata](evidence/162-test-file-dependency-overlays/archive.json)
records entry hashes, source base revision and exclusions; every entry and source
was hash checked. Build binaries and compiler temporary files are excluded.
The policy preflight embeds its 30 verifier outcomes and protocol evidence in its
report; its isolated scratch projects are automatically removed by that existing
runner and are not claimed as preserved snapshots. Future participant trials
must retain their frozen projects independently.

See [test-file syntax](../docs/TEST-FILE-DEPENDENCIES.md),
[the executable example](../examples/test-file-settings.agent), and
[library I/O testing](../docs/LIBRARY-IO-TESTING.md).
