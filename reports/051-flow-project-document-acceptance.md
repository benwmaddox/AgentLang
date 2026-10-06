# Flow project-document Runtime acceptance

Status: test fixtures added; Runtime integration and validation are pending,
2026-10-06. This report records acceptance intent and the pre-integration
foundation evidence only. It does not claim project-document Runtime success.

## Scope

The assigned test change is limited to
`tests/AgentLang.Flow.Runtime.Tests/Program.fs`. It exercises the existing
`define` protocol with `frontend: "flow"` and a complete Flow document that
declares a `Customer` record, refined `Email` scalar, `MetersPerSecond` nominal
Float wrapper, validator and dependent Flow words, plus inline tests/examples.
The positive fixture checks the authored type-member bytes and hashes, Flow/1
type metadata, stable validator `UserWord` identity, type-only selected commit
closure, nominal constructor/accessor behavior, source inspection, reload,
fresh-process CLI evaluation, and canonical aggregate export as a separate
projection.

Negative fixtures cover existing word/type collisions, a later invalid word,
wrong nominal constructor payload, validator signature/effect rejection, an
invalid refined-value constructor with an expected `REFINEMENT_FAILED` result,
and temporary typed documents. They compare the visible word/generated-word
inventory before and after failure to catch partial activation. A separate
candidate discard case, task abort and named snapshot restore check type-source
removal and exact manifest authority restoration.

## Validation status

The independent foundation checkpoint passed a fresh Release solution build
with zero warnings/errors and the existing Flow (964 assertions), Storage
(15 groups / 305 assertions), Flow Runtime (15 groups / 399 assertions), and
language Acceptance (34 groups / 583 assertions) suites. Those checks predate
the new Runtime project-document route; they validate parser/Storage foundation
only. Evidence is recorded in [report 050](050-flow-project-documents.md).

The new fixture is saved and frozen for the root-owned serial validation slot.
The fifth fresh Release solution build passed with zero warnings/errors;
evidence is saved in `evidence/050-fifth-solution-build.json`. The first
Runtime execution reached the new fixture and rejected its initial full-document
`define` with `FLOW_EXPECTED_TOKEN`, because the in-progress Runtime path still
uses the single-word Flow parser. The failure is saved in
`evidence/050-second-flow-runtime-tests.json`; the document fixture remains
unchanged pending the atomic project-document route. The focused Flow suite also
passed 971 assertions including the nesting-guard repair, in
`evidence/050-second-flow-tests.json`.

The atomic project-document Runtime route is now present. The sixth fresh
solution build stopped in the new Runtime builder on ambiguous record/scalar
helper inference before the Runtime acceptance executable ran; diagnostics are
saved in `evidence/050-sixth-solution-build.json`. The Runtime owner is adding
explicit type annotations. No result from the new project-document assertions
is claimed yet.

After those annotations, the seventh fresh Release solution build passed with
zero warnings/errors (`evidence/050-seventh-solution-build.json`). The focused
Flow Runtime suite passed 16 groups / 491 assertions, including the new
project-document acceptance fixture (`evidence/050-third-flow-runtime-tests.json`).
The full fresh 26-check Release validation gate passed; the build reported zero
warnings and zero errors. Evidence is saved in
`evidence/050-publication-validation.json`. Committed-source CI is still pending
at this report revision. The earlier foundation pass and expected pre-route
failure above are retained as history, not used as evidence for the new
project-document Runtime feature.
