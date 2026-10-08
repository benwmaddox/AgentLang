# Finite library coverage

Status: implemented and validated locally for the finite-library-coverage
milestone. The 37-check gate in [report 116](../reports/116-finite-library-coverage.md)
is historical evidence for that milestone. The separate aggregate Debug gate
for whole-record validators in [report 119](../reports/119-record-construction-invariants.md)
passed 36/37 checks; the remaining inspection script was repaired and its exact
probe passed 31 assertions separately. The full aggregate was not rerun.

Authored library qualification requires three separate forms of evidence from the
function's own passing attached tests: executable instruction/branch coverage,
finite input coverage, and finite declared return coverage. Compiler match
exhaustiveness remains mandatory independently of test coverage or maturity.

## Inputs and returns

For each Bool or enum parameter, observe every valid declared value at actual
entry to the tested function. Track parameters independently. A pair of Bool
parameters requires both values for each parameter, not automatically all four
input combinations. Interaction tests remain important; full Cartesian input
coverage and MC/DC are separate research decisions.

For each declared return position, observe both Bool values, every enum case,
and Unit where declared. Separate return positions do not imply a Cartesian
product; a finite composite within one position does.
Option and Result require each inhabitable alternative. Fully finite payloads
require every declared value, including combinations within a finite composite.
Int, Float, String and numeric Money are not exhaustively enumerated. Composite
returns containing such payloads still require their applicable alternatives;
boundary and behavior assertions remain necessary. An open record with two Bool
fields and an Int field requires each Bool field's values, not an implicit
Cartesian product across these projections. A nested fully finite record field
still requires all of its values. Tests of interactions across open-record fields
remain a separate behavioral obligation.

An unreachable declared finite return value is not exempt. Narrow the return
contract truthfully or keep the function at project maturity. Refined nominal
types must retain their validity constraints; a validator does not make its
valid values enumerable. Recursive domains, refined finite domains without a
proven valid-value set, and expansions beyond 4,096 values are unsupported.
Qualification must fail explicitly; a partial enumeration must never be
reported as complete coverage.

The same rule applies to a function that returns a validated record with finite
projections: until its complete valid-value domain is proven, exhaustive return
qualification remains unsupported. This does not prevent a predicate whose
input is that record from qualifying on a finite Bool result. Focused
`lookup.valid?` tests cover both match branches and observe both `true` and
`false`; the false return is recorded when the constructor raises its expected
validation error. This qualifies that predicate, not a function returning the
validated record.

## Observation boundary

Observe the verified function identity and revision at entry and normal return.
A helper's return, an expected-result expression, a literal elsewhere in a test,
or an earlier interactive evaluation cannot supply the target's evidence.
Multiple actual target calls in one passing attached test may contribute. At least
one actual invocation is required even for an empty identity body with open input
and output types; an unrelated passing attachment is insufficient.
A target invocation that throws has no normal return to count. A passing
expected-error test can still contribute returns from target invocations that
completed before a later error—for example, a record validator returning false
before its constructor rejects the record. An exception is never treated as a
return value. Failed value, error-code, or effect assertions cannot supply
qualifying evidence.

Observation must not eagerly decode normal interpreter or retained mailbox
values. The host requests decoded input/output observations only for a matching
test target. Preserve balanced scope cleanup on faults and preflight denial.

## Publication and durability

Apply the gate to the exact proposed dictionary before atomic publication.
Replacement must revalidate affected library callers with their own tests;
rename, deprecation and reload must not preserve a stale qualification silently.
The initial implementation rejects load with a structured diagnostic if an
existing durable library fails requalification, leaving its files unchanged.
A repair-only loading mode is outside this slice.
Expose required, observed and missing finite values deterministically, separately
for each parameter and return position, alongside existing structural coverage.

Enum-bearing functions become eligible only where these obligations can be
established. Removing the current enum guard is not evidence that native enum
execution, module visibility or library dependency-closure rules are complete.
Those requirements retain their own implementation and validation boundaries.

## Acceptance

Cover branchless Bool functions; independent input parameters; nominal enum
identity; complete and incomplete Option<Bool>/Result alternatives; unsupported
or oversized domains; expectation contamination; failures excluded from qualifying
evidence; nested and repeated calls; and atomic rejection across publication,
replacement, rename, deprecation and reload. Exercise the lazy interpreter hooks
separately, including faults and retained-state execution. Existing library
fixtures must add real missing cases or use truthful narrower/project contracts;
do not grandfather them merely to keep tests green.

## Adoption evidence

[Report 117](../reports/117-finite-coverage-adoption.md) records one fresh agent
adding both missing Boolean combinations and leaving an unreachable declared
Option alternative at project maturity. Its tests reject both frozen Boolean
mutants. A scripted three-row control nevertheless passes this gate while
missing one interaction bug. The current per-parameter rule is not Cartesian
coverage. One redundant actor-added test also shows that passing qualification
does not establish the value of every test. These are bounded observations,
not a comparative correctness claim or a change to the qualification contract.
