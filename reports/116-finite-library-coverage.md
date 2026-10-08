# Finite input and return coverage for libraries

Validated locally: all 37 full Debug gate checks passed.
This is an implementation result, not an agent-efficacy result.

## Delivered behavior

Library qualification now combines own-body instruction/branch coverage with
finite input and return observations from the exact verified function ID and
revision. Direct Bool/enum parameters are checked independently. Finite returns
include Bool, Unit, enum cases, Option/Result alternatives and finite record
products. Open composite returns retain applicable finite projections. Open
refined String/Int/Float types remain open; unproven refined finite domains,
recursive domains and domains exceeding 4096 values reject qualification.

Passing own tests must actually invoke the target, even for an empty identity
body. Failed assertions and expectation evaluation contribute no evidence.
Expected-error tests can contribute inputs but no returns, including a normal
return earlier in the same test. Introspection exposes required, observed and
missing values separately for each parameter/return position.

Publication and reload use the same qualification gate before activation.
Replacement rechecks affected library callers; rename, deprecation and named
snapshot restoration also requalify. Invalid stored libraries fail to load
without modifying their files. Enum-bearing authored helpers must independently
qualify. Complete module visibility/dependency closure and native enum execution
remain pending. No manifest or native ABI version changed.

## What the stricter gate found

The existing business vocabulary had three reachable return cases missing from
attached tests: cancelled subscriptions returned by `store.subscription`,
`store.subscription-fold-step`, and `subscription.replace-step`. Added tests
exercise real cancelled values, including the populated cancellation timestamp.

The email fold helper exposes a different limitation. It always populates
`EmailDeliveryPlan.first`, but its declared Option field also admits none.
No honest test can produce that alternative. The helper and its sole authored
caller remain project functions; the other 51 functions qualify as libraries.
This preserves behavior and the strict rule. A narrower contract would be needed
to qualify that helper. Recursive finite-return requirements therefore create
friction for accumulator types that admit more states than a step can return.

Fresh growing seeds contain 53 functions, 31 types, 154 tests and 44 examples.
Preparation uses the existing Core parser to retain the two project functions'
exact declarations and attachments while the eligible set is committed together.
It verifies restoration and the 51-library/2-project inventory after reload.
Historical trial inputs and reports are unchanged.

Two constant-Bool negative controls now fail qualification before publication,
despite passing their own tests and structural coverage. Three wrong Float
controls still qualify and are rejected by independent behavioral checks. This
shows both the added protection and the continuing need for independent oracles.

## Validation and corrections

Focused local checks passed:

- Finite-domain IR tests: 20 groups / 179 assertions.
- Interpreter observation hooks: 76 assertions; active call bindings: 31.
- Flow runtime: 29 groups / 906 assertions.
- Business language: 7 groups / 6,325 assertions.
- Business transitions: 7 groups / 4,304 assertions, including all-function
  finite audit, atomic refusal and exact maturity/source/metadata reload.
- Fresh growing seed preparation: exact 53/31/154/44/51/2 inventory.
- Native-mailbox host adapter: fresh isolated build, zero warnings/errors.
  This checks host API compatibility, not native coverage or enum execution.

The first full run passed 33/37 checks and exposed an overbroad refined-scalar
classifier plus a stale negative-control assumption. After correction, the
second passed 34/37 and exposed the business return gaps above. Both failed runs
are retained. Review also caught return-evidence leakage in expected-error tests;
a regression now verifies its rejection. The final full run passed all 37 checks,
including 98 business-policy checks across 30 independent outcomes. The solution
build reported zero warnings/errors. Tests ran locally; CI configuration is unchanged.

## Limits and next research check

This does not prove correct business behavior: complete observed alternatives can
still have incorrect assertions. Cartesian input products and MC/DC remain
separate decisions. Mixed finite/open observations can decode large open payloads;
directions without finite obligations skip decoding. Durable library loading now
reruns qualification tests; no startup-performance improvement is claimed.

The next bounded agent check should test whether a fresh subagent discovers these
diagnostics, supplies meaningful missing cases, and chooses an honest project or
narrower contract when an alternative is unreachable. Report 115's effect-test
adoption result does not establish adoption or comparative reliability here.

## Full gate provenance

Command: ./scripts/Validate.ps1 -Configuration Debug -ReportPath .agentlang/finite-coverage-001/final-full-validation.json.

The final run tested dirty revision 36f78b2; [tested-source.json](evidence/116-finite-library-coverage/tested-source.json) identifies the exact implementation and test bytes. Source remained unchanged during the final run. All ten frozen Release runtime files from report 108 remain unchanged. Evidence retains both earlier failed full runs, the final successful run, focused checks, review findings and the fresh seed inventory. No new agent trial was run in this milestone.
