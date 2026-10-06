# External-agent refactoring smoke

One fresh Luna/max agent must remove duplicated classification and discount
logic by discovering and composing retained vocabulary. This is a bounded
Category E workflow test, with no matched cost or model-context claim.
The [completed review](../../../reports/069-existing-vocabulary-agent-refactoring.md)
records 96 passing independent checks and 88 state/structure checks, with exact
test preservation and verified dependency consolidation.

The coordinator seeds duplicate but behaviorally correct versions of two
library words through ordinary replacement gates. All 21 self-tests and both
independent 20-vector behavior sets pass before launch. The required structural
change is checked separately: the shared balance word directly uses the existing
premium predicate; renewal directly uses shared balance and annual eligibility;
neither retains direct classification primitives. Behavioral parity alone does
not count as a successful refactor.

Preserve the exact type definitions, stable word identities, all existing tests,
unrelated words and historical revisions. Archive source/runtime/host/prompt
pins before launch, starting/final trees, raw protocol trace and acceptance.
The actor uses only its isolated project's protocol and leaves its live host
session open for separate coordinator teardown.
