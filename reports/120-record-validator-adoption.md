# 120 — Record-invariant adoption: correct behavior, incomplete qualification

One fresh external Luna/max agent built and persisted the requested range
invariant. Independent reload checks passed all nine frozen cases and all five
agent-written tests. However, it left the validator at project maturity and
incorrectly concluded that its false return could not be tested. The behavior
objective passed; the request for the highest justified library maturity was
not fully achieved.

## Trial fixed before dispatch

The actor started in an empty isolated Flow/2 project with no conversation
history, repository access, hidden oracle or control implementation. Its task
was to create `BoundedRange { start: Int; finish: Int }`, reject reversed ranges,
allow equal endpoints, implement `range.width`, attach useful tests and commit
at the highest justified maturity. The prompt described behavior, not validator
syntax. It supplied a short language primer and the existing JSONL broker with
a 100-exchange limit. The actor received no coordinator solution hints during execution; runtime
help remained available as intended.

The prompt, plan, nine-case oracle, broker, control evidence and 22 runtime files
were hashed before dispatch. The Debug binaries came from the tested dirty
implementation based at `db481e2`; that implementation was published as
`0c6a00d` before dispatch. Exact artifact hashes identify the runtime rather than
a claim that it was built from a clean committed checkout.

Preflight distinguished a correct implementation from an always-true validator.
The correct control passed three tests and all nine cases. The mutant admitted
all four reversed ranges and failed its expected-error test. Scorer review
added exact test-count/name/failure checks before freezing. These controls did
not test persistence; the completed actor was subsequently checked after reload.

## Observed result

| Check | Result |
| --- | --- |
| Five valid ranges, including equality and negative bounds | All widths correct |
| Four reversed ranges | All rejected with `RECORD_VALIDATION_FAILED` |
| Actor-owned attached tests after reload | 5/5 passing |
| Fields, function signatures and validator purity | Match the task |
| Persisted validator target | Matches the predicate's stable WordId |
| `range.width` maturity | Library |
| `range.is-valid` maturity | Project; false return missing from its own tests |
| Scoring isolation | Original actor project's 14 files unchanged |

The relevant actor code is:

```agentlang
record BoundedRange {
    field start: Int;
    field finish: Int;
    validate range::is-valid;
}

fn range.is-valid(value: BoundedRange) -> Bool {
    int::less-or-equal(value.start, value.finish)
}
```

The actor attached its reversed-construction test to `range.width`. That test
correctly rejected an invalid argument before width executed, but did not count
as the validator's own qualification evidence. The validator's two tests both
observed true. The actor inspected the missing-false coverage and never
attempted to commit the predicate as library. Thus there was no library refusal
to blame for its final conclusion. Generated record helpers appear at library
maturity automatically; this is not separate actor-authored qualification.

A **separate coordinator diagnostic**, after the trial, reconstructed the same
function bodies and five tests in a new project and added one case:

```agentlang
test range.is-valid/reversed-library-evidence {
    BoundedRange(start = 8, finish = 2)
    => error RECORD_VALIDATION_FAILED
}
```

Its three validator tests observed both false and true. The predicate then
committed at library maturity, and all six tests passed. The actor's source was
unchanged. This demonstrates an available authoring route, not an actor success
or a retest of the blind trial. That diagnostic did not independently reload
its committed project, and did not attempt to qualify width.

## Discovery and friction

The actor used 50 broker exchanges. The broker session lasted 491.9 seconds,
from `session-start.startedUtc` through `session-end.finishedUtc`, including
time before the first exchange and after the last. Payloads totaled 6,966
request bytes and 63,791 response bytes; these are not model tokens. Provider
token counts and model-turn usage were unavailable. The broker and runtime
exited zero with no runtime stderr.

There were 12 error responses: a search argument mismatch, five declaration
syntax errors, two attempts to inspect a type through a word selector, three
evaluations without the required code field, and an attempt to attach authored
tests to a generated constructor. The agent recovered sufficiently to deliver
correct behavior. It first retrieved default Flow/1 help, then Flow/2 define
help before authoring. That help did describe validator false-return observation,
but its prose did not prevent the test-ownership misunderstanding.

The coordinator's first scoring attempt also used the wrong type-source
selector. It is preserved separately; the scorer was corrected to the supported
API and the second attempt passed. This harness error is not an actor failure.

Review also clarified that `describe.callers` and the legacy `callers` operation
list authored callers, while `search-dependency` and `transitive-callers` include
generated constructor edges. The graph contains the validator edge; its views
have different scopes. This was not shown to cause the actor's maturity choice.

## Interpretation and next step

The construction mechanism was usable by one unfamiliar agent, and enforced the
right boundary after persistence. The stronger claim—that discoverability and
coverage metadata reliably lead an agent to fully qualified reusable vocabulary—
was not met in this case. A visible coverage gap did not produce the right repair,
even though the existing language could express it.

There is also a type-model tension worth researching: the predicate accepts
`BoundedRange`, but during construction it receives an unchecked candidate of
that nominal type. For ordinary callers, only validated ranges exist. The
actor's explanation is consistent with applying that ordinary-caller assumption
to the validator itself. Better help may suffice, but a distinct construction
input or field-based predicate could make the boundary easier to reason about.
This trial does not determine which design would work better.

The next bounded improvement should clarify authoring, without weakening gates:
provide a complete validated-record example with a **validator-owned** rejection
test, make type-source selection explicit, and document generated-caller views.
Then test a fresh actor on a comparable unfamiliar invariant. Do not count a
coached repair of this same actor as independent adoption evidence.

This single guided task has no conventional comparator. It establishes no
general reliability advantage, retention benefit, token saving or native
performance result. The nine small integer cases do not evaluate overflow policy.

## Validation and evidence

Product validation is recorded in [report 119](119-record-construction-invariants.md):
36/37 aggregate checks passed and the remaining inspection-script repair passed
its exact 31-assertion command separately. This milestone changes reports and
research artifacts only, so it does not rerun the product suite. CI remains
manual-only.

The focused independent command was:

```powershell
& .agentlang/record-validator-adoption-001/scoring/Score-Actor.ps1 -ActorProjectPath .agentlang/record-validator-adoption-001/project/actor -RunId final-02
```

[Evidence index](evidence/120-record-validator-adoption/index.json) covers the
frozen inputs, raw actor trace/final response, controls, both scoring attempts,
project states, review and explicitly separate coordinator diagnostic. Frozen
input integrity, original-project preservation and publication-byte checks are
recorded. Runtime binaries are excluded; replay requires the pinned artifacts
or a documented new runtime build.
