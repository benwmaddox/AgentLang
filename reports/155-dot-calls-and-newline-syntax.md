# 155 — Dot calls and newline-first source

Status: implemented; all 37 local Release checks pass.

The user approved three source changes together: dotted qualified calls,
newlines as normal separators, and expression-only match arms without braces.
This updates current Flow/2 rather than creating another syntax version. Typed
semantic IR, execution order, effects and exhaustive matching remain the
semantic contract. Native Option/Result investigation is deferred while this
authoring change is implemented.

## Delivered behavior

- `file.read(path)` names an exact qualified dictionary function when `file`
  is not a lexical local. `customer.email` remains a property, and
  `customer.balance()` supplies the local `customer` as the first argument.
- A lexical root takes precedence with no namespace fallback after a local
  lookup or type failure. `.customer.balance(customer)` explicitly selects
  the dictionary function when its namespace is shadowed; `.identity(value)`
  selects an exact unqualified key.
- Constructors, validators and static callbacks use dots. Formatting preserves
  the existing direct, receiver and exact-root call identities and paths;
  legacy `::` source remains readable without rewriting retained source on load.
- Newlines separate statements, record fields, validation declarations, enum
  cases and effect-count entries. Semicolons still separate multiple entries
  on one line. Canonical formatting omits optional semicolons.
- A simple match arm can be `pending => "Pending"`. Multi-statement arms
  retain braces. Both forms lower to the existing case-body representation.
- A line-leading dot begins an exact dictionary call, not continuation of the
  previous expression. Keep postfix dots on the receiver's line. Discovery
  references include `flowReferenceSyntaxVersion: 2` so consumers select the
  correct parser when copying them.

## Acceptance and validation plan

Focused tests cover parser/formatter idempotence, qualified calls versus lexical
receivers, property access, decimals, containers, callbacks, validator names,
enum/Option/Result matching, nested branches, binding timing and scope isolation.
Runtime tests cover effects, stored call attestation, legacy source reload,
rename/reload and shadowed namespaces. Nine current Flow/2 example files were
formatted with the fresh CLI and checked for idempotence. Frozen studies and
historical Flow/1 fixtures were not rewritten.

Validation used fresh serial Release builds of the Flow and Flow runtime
executable test projects, applicable call-binding checks, then the complete
local Release gate. Failed attempts are preserved alongside the final results.
This is an authoring change motivated by user feedback, not a new comparative
agent-efficacy result.

## Validation observations

The first fresh CLI build exposed two F# typing mistakes in the implementation;
both were corrected before executing examples. All four complete code blocks in
the current syntax guide define successfully and their two attached tests pass.
The nine current Flow/2 example files format idempotently.

Example migration exposed a semantic formatter defect: integral-valued Float
literals such as `100.0` were printed as `100`, so reparsing changed their type
to Int. Textual idempotence alone did not catch that error. The formatter now
retains a decimal/exponent marker for Flow/2 Float literals, and focused regressions
check literal type, value and negative-zero sign. The initial failed customer
definition is retained in the evidence; after repair its four tests pass.

The expanded Flow parser/compiler suite passes 1,214 assertions. The Flow
runtime suite passes 33 groups and 1,205 assertions. The source suite passes
101 assertions after updating its canonical newline expectation, and the
call-binding probe passes 31 assertions. The final full Release gate passes all
37 checks, including the business-policy preflight's 98 checks across 30
independent expected outcomes. The final solution build has no warnings or
errors. This gate took 33.8 minutes, of which 26.1 minutes were the preflight.

Independent review caught a dotted-name lookahead that could absorb a newline
and an unintended relaxation of Flow/1 record/scalar separators. Both were
fixed. Added checks cover failed local-stage resolution without namespace
fallback, static callbacks despite a shadowing local, and fresh-engine reload
of unchanged legacy Flow/2 source before rename. Formatting checks also cover
nested initializer indentation and preservation of Flow/1 semicolons.

Six example execution groups pass their attached tests: customer (4), containers
with refined types (21), closed renewal states (4), money ratios with the
business foundation (112), nonempty intervals (8), and refined types (6).
These counts overlap dependency tests and are not a unique-test total. Two
initial ad-hoc execution attempts omitted required fixture dependencies; their
errors are retained alongside the corrected run. Early test compilation errors,
an incorrectly typed new Result fixture, and stale source-format expectations
are retained in the validation logs as well.

The first broad gate exposed two stale discovery-metadata expectations and
three frozen-snapshot reload failures. The latter were a genuine compatibility
regression: preserving `.0` for Float literals also changed regenerated
historical Flow/1 project exports. Literal rendering is now version-aware:
Flow/1 retains its historical export bytes, and current Flow/2 preserves Float
types. Snapshot fixtures and strict manifest/export equality remain unchanged.
The failing run was explicitly stopped during its long business preflight,
with its log and completed component evidence retained. All five previously
failing checks pass in focused reruns and in the fresh complete gate.

The complete gate command was:

```powershell
pwsh -NoProfile -File scripts/Validate.ps1 -Configuration Release -SerialBuild -SkipPackageAudit -ReportPath .agentlang/dot-calls-155/validation-002.json
```

It ran on a working tree based on `c131d11`, with local temporary directories.
NuGet network vulnerability auditing was disabled explicitly; package builds
and all required local acceptance checks still ran. CI remains manual-only.

## Demonstrating library qualification with simulated I/O

In response to the user's testing question, a runnable
[library I/O example](../docs/LIBRARY-IO-TESTING.md) exercises the real function
against fresh per-test virtual files. Four tests cover both conditional paths,
the Bool fallback parameter, both Bool returns and exact read/write counts.
Coverage reports 8/8 instructions and 2/2 branch outcomes. All four pass and
library commit succeeds. A one-case version has a passing
test but fails publication with `LIBRARY_COVERAGE_INCOMPLETE`. An extra-read
mutant preserves returned values but fails two effect assertions and commit
with `COMMIT_TESTS_FAILED`. Raw requests, responses and project states are
retained under `library-io-demo-001` in the milestone evidence.

This uses existing virtual providers. User-authored test-scoped dictionary
overrides remain planned, and real external adapters require their own
integration tests.

## Evidence and limits

The [evidence archive](evidence/155-dot-calls-and-newline-syntax/evidence.zip)
contains 201 entries (2,104,641 bytes), including both gate attempts, the review,
example runs, library-promotion controls, and implementation/source hashes.
SHA-256: `8e4c5ac21f0b61426b2728f1f8021eb3c39d64ea2c1b87defd2c8522890ed530`.
See its [archive manifest](evidence/155-dot-calls-and-newline-syntax/archive.json).

Select Flow/2 explicitly in current clients; omitted version selectors still
use Flow/1. The legacy formatter keeps its historical Float representation for
reproducible exports. These are implementation and regression results, not a
new measurement of agent reliability, context cost, or native performance.
