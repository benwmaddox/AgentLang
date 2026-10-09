# Library dependency qualification validation

This implements an existing PRD requirement after report 141. It does not add
module boundaries or claim an agent reliability advantage.

## Contract

A qualified library function may transitively call only qualified authored
library functions, trusted primitives, and generated type operations. Static
callbacks count as dependencies. Ordinary project functions retain the lighter
gate. Library intent is insufficient: all selected library candidates must pass
their own attached tests and coverage requirements before atomic publication.

Replacing a dependency must recheck affected library callers against the
proposed dictionary and run their required tests. Failure must preserve the old
durable dictionary. Reload must reject a persisted library whose dependency
closure does not satisfy the contract. Reuse the current bound snapshot and
revision machinery; do not introduce a separate qualification cache.

Explicit durable module membership, cross-module visibility and entry-point
rules remain separate approved work. Do not infer modules from dotted names.

## Implementation acceptance

- Reject direct, indirect and callback calls into unqualified authored helpers.
- Permit trusted/generated operations and ordinary-to-ordinary composition.
- Permit qualified composition and atomic publication of an acyclic selected
  group when every library member passes its own gate.
- Reject an unsafe helper replacement without changing durable state; reload
  the preserved old project successfully.
- Reject unchecked persisted dependencies at load.
- Preserve finite input/return coverage, instruction/branch coverage, checked
  types/effects and affected-caller tests.

Run focused runtime tests first, then the repository's local validation gate.
Do not downgrade library fixtures merely to make validation pass. Freeze the
runtime before the external-agent recovery probe, after focused tests, LLVM
conformance and the probe's controls pass. The long historical business-policy
preflight may finish concurrently; publication still requires every local gate.

## Evidence and next agent task

The pre-change CLI from report 141 accepts a library wrapper over an ordinary
integer helper even though both functions have passing tests. The corrected
baseline requests and responses are retained under
`.agentlang/library-closure-142/baseline-corrected/`. An earlier coordinator
attempt used unsupported infix addition and failed parsing; it is retained
separately under `baseline/` and is not evidence about qualification.

First use a small external-agent recovery probe to check that the diagnostic
leads to qualifying and testing the helper, rather than weakening the caller.
Keep structural enforcement separate from evidence of agent adoption.

A subsequent matched business task can prevent overlapping active subscription
intervals for the same customer and product. Use the accepted report-135 paired
projects, without report 141's imported-ledger scaffold. Define intervals as
half-open before freezing a task; include overlap in both directions,
containment, adjacency, customer/product isolation, cancellation, preserved
validation precedence and unchanged input state. Use only reachable states
constructed through existing public APIs. Assess resulting regression protection
through a plausible overlap-boundary mutation, not whether an agent duplicates
existing assertions. Do not dispatch before equivalent APIs and positive/negative
scorer controls have been checked. This is a proposed task, not a frozen study.

## Probe outcome

The frozen guided probe is complete: independent acceptance passes, both functions
are qualified, all inherited tests remain and one new helper test rejects the
controlled defect. See report 142. This supports adoption of the contract; it does
not establish diagnostic-driven recovery or comparative reliability. The proposed
matched subscription task in the original plan remains undispatched.
