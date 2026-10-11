# Semantic type inspection and test execution counts

The fresh maintenance comparison exposed two tooling problems: a source-based
record contract check treated legal semicolons as part of field types, and an
agent described cumulative test executions as the size of a single test suite.
This change addresses the runtime interfaces that future tools and agents can
use. It does not repair the historical scorer, participant endpoint or result.

## Checked type structure

`describe` with `type` now supplements existing source/revision metadata with
the checked definition's `typeKind`. Records expose ordered `fields` containing
`name` and canonical `type`; scalars expose `baseType` and `validatorName`; enums
expose ordered `cases`. Nominal field types retain their names. Consumers can
check a record contract without interpreting source whitespace or separators.

Candidate and persistent definitions use the same representation. Source hashes
still identify exact text and may differ for equivalent source spellings.

## Execution work versus suite size

Test-run responses include `testSummary` with `executed`, `passed` and `failed` for
the full execution batch. `failed-tests` still returns only failing rows, but
its summary counts the entire rerun. Task status and saved logs expose
`testExecutions` and `testFailures` as explicit names for the existing cumulative
`testsRun` and `testsFailed` totals. Repeated runs and publication gates count
again. These totals do not measure distinct tests or establish domain correctness.

## Scope and validation

The change adds read-only metadata and execution accounting. It does not grant
effects, add primitives, introduce network services or enable arbitrary .NET
invocation. Test dependency replacement remains scoped to test execution.

The read-only boundary review found no test-override capability bypass: the
original call's effects are checked before fixture dispatch, fixture effects
are constrained to its contract, and attached tests use fresh virtual files.
It confirmed the existing real-filesystem limitation documented in the README:
path and reparse-point checks do not provide an OS sandbox against pre-existing
hard links or concurrent filesystem changes. The configured project root is a
host trust decision. This change neither fixes nor expands that boundary; use
the virtual provider or external OS isolation for untrusted filesystem inputs.

The corrected Flow runtime suite passes 45 groups and 2,260 assertions. The
freshly built LLVM suite passes 950 assertions, including native layouts,
refinement admission, mailbox policies and interpreter parity. Fresh builds
have zero warnings/errors, and whitespace checks pass. Dependency restore ran
with NuGet auditing enabled. Local persistence tests require host execution;
the sandbox denies their existing temporary-file operations.

Initial verification caught F# assertion syntax errors, invalid inline metadata
in a new Flow fixture and a missing attached validator test. Corrections are
limited to the new test group; existing test bodies remain byte-for-byte
unchanged. The original full-run failure is retained, alongside the corrected
suite receipt, rather than relabeled as a passing run. The full local Release
run records 36 passing checks and that one failed runtime check. Rebuilding and
rerunning the corrected runtime suite completes the outstanding check without
changing production runtime code or other test bodies. The final business-policy
preflight passes 98 checks across 30 independent positive/negative outcomes.
The [validation summary](evidence/223-semantic-inspection/validation-summary.json)
states this combined result explicitly; the original full-run report remains
`passed: false`.

The [source review](evidence/223-semantic-inspection/review.md) and test receipts
record the focused validation and boundary assessment. The
[archive index](evidence/223-semantic-inspection/index.json) seals the raw full-run,
initial build-failure and business-policy preflight records by length and SHA-256;
every compressed member is verified against the original bytes.
The [previous result](222-typed-batch-fresh-results.md) remains a
negative preservation finding and establishes no comparative reliability lead.
