# 121 — Executable record-validation guidance

Flow/2 runtime help now provides a complete record-validator example, including
predicate-owned valid and rejected-construction tests and the exact requests to
qualify the predicate as a library function. This addresses the discoverability
failure observed in [report 120](120-record-validator-adoption.md); it does not
change the type system, coverage rules or constructor behavior.

The `TutorialSpan` example explains that validation receives an unchecked
construction candidate. Its own reversed-construction test observes the
predicate's completed false return before `RECORD_VALIDATION_FAILED`. A rejection
test owned by a caller does not qualify the predicate. Generated constructors
cannot own authored Flow tests for it.

Help distinguishes `eval.code`, `define.source`, `source.word` and `source.type`.
Discovery documentation also identifies the authored-only caller views and the
queries that include generated constructor edges. Flow/1 help and existing
Flow/2 examples remain available.

## Validation

- Fresh `dotnet build AgentLang.sln --configuration Debug`: zero warnings/errors.
- `dotnet run --no-build -c Debug --project tests/AgentLang.Flow.Runtime.Tests`:
  30 groups / 1,023 assertions passed.
- `dotnet run --no-build -c Debug --project tests/AgentLang.Cli.Tests`:
  11 groups / 145 assertions passed.
- Independent scoped review: no blocking findings; `git diff --check` passed.

The acceptance checks execute the requests returned by help, verify both Bool
returns, commit the predicate as library, reload the project and check exact
word/type source, purity and invalid-construction rejection. An initial build
failed on F# syntax/type inference in the new test code; the repaired build and
original failure are both retained. The full policy preflight and native suite
were not rerun for this help/documentation change. Report 119's aggregate result
remains 36/37 with the repaired inspection check passed separately.

## Research follow-up

A fresh isolated agent will receive a new bounds invariant with positive minimum
and ordered endpoints, a frozen behavioral oracle, the updated runtime and no
solution hints. Behavior, persisted bindings and library maturity will be scored
separately. This is a usability probe, not a causal comparison: the task differs
from report 120. No fresh-agent result is claimed by this milestone.

Distinct construction-input types or field-based predicates remain an open
research question. Better guidance does not settle whether the existing nominal
candidate model is the best design.

Raw build/test logs, source hashes, plan and review are in
[evidence](evidence/121-record-validation-guidance/index.json). The build used the
working source based on `cbe91dd`; exact tested source hashes are recorded in
`results.json`. Historical trial runtime copies were not rebuilt or edited.
