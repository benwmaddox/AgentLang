# 123 — Versioned help exposed; validator adoption succeeds

A fresh external Luna/max agent read the new Flow/2 validator example and
completed the same BatchBounds task used in report 122. Independent reload
verification passed all ten frozen cases and all seven agent-written tests.
Both authored functions remained library-qualified. There were no structured
error responses or publication refusals.

This closes the help-exposure gap. It demonstrates that an agent can use the
available guidance, not that guidance caused the cleaner result or that the
language outperforms a conventional repository.

## Fixed design

Only the actor prompt's trial paths and one help instruction changed from
[report 122](122-record-validation-help-adoption.md). The added instruction
requests `{"op":"help","topic":"define","syntaxVersion":2}` before defining
code, verifies the returned version, and selects version 2 for later help calls.
There were no added domain or solution hints.

The public task, ten-case oracle, broker, 100-exchange cap, exact 22 runtime
binaries and reviewed scorer were unchanged. The runtime is the frozen report
121 Debug build associated with implementation commit `ee97dda`; repository
HEAD at dispatch was `c9c8fce`. No new build, controls or harness were needed.
Prior correct/fault control evidence from report 122 was reused by verified
hash reference, not rerun or claimed as new evidence.

The fresh actor had no inherited conversation and received no coordinator hints
during execution. It used a new empty project through the same broker. File and
cross-trial access restrictions were instructions plus the broker surface, not
an OS security-isolation claim. Inputs and scripts were frozen before dispatch.

## Observed behavior

The agent requested Define help at exchange 4; the response explicitly returned
syntax version 2 and included the complete TutorialSpan example. Authoring and
Examples help also returned version 2. Both definitions succeeded on their first
attempt. The validator's own tests included two valid inputs and separate
rejections for a nonpositive minimum and reversed bounds.

The authored predicate was:

```agentlang
fn batchBounds.valid?(value: BatchBounds) -> Bool {
    doc "Returns whether the batch bounds have a positive minimum and an ordered maximum."

    bool::and(
        int::greater-or-equal(value.minimum, 1),
        int::greater-or-equal(value.maximum, value.minimum)
    )
}
```

It used pure effects by default, documentation followed by a blank line, and
plain record properties. The actor committed both functions as libraries and
successfully requested `source` with `type: "BatchBounds"`, as well as both
function sources. Its broker closed with host and runtime exit zero.

| Independent check | Result |
| --- | --- |
| Four valid cases and six invalid constructions | 10/10 correct |
| Attached tests after fresh-process reload | 7/7 passing |
| Validator-owned tests | Two valid, two rejected constructions |
| Validator finite return evidence | false and true observed; complete |
| Span-owned value tests | Three passing |
| Authored predicate and span | Both pure library functions |
| Record fields and exact function signatures | Match task |
| Persisted validator binding | Same user-word identity in manifest and reloaded description |
| Original actor state | All 18 files unchanged by copied-state scoring |

## Descriptive comparison

| Measure | Report 122 | This trial |
| --- | --- | --- |
| Help actually returned | Flow/1 | Flow/2 |
| Independent behavior | 10/10 | 10/10 |
| Final own tests | 6/6 | 7/7 |
| Both authored functions library | Yes | Yes |
| Broker exchanges, excluding close | 47 | 29 |
| Structured error responses | 11 | 0 |
| Request payload bytes | 7,402 | 2,484 |
| Response payload bytes | 59,235 | 52,972 |
| Full broker session seconds | 391.323416 | 220.618454 |

The session boundary includes initial idle and close. Payload bytes are not
model tokens; provider usage and model-turn counts are unavailable. Two fresh
actors are stochastic samples, not a controlled estimate of improvement.
The task remains small, with overflow outside scope, and these results do not
prove general business correctness or smaller-context robustness.

## Validation and next action

The unchanged scorer ran once and exited zero:

```powershell
pwsh -NoProfile -File .agentlang/record-validation-help-adoption-002/scoring/Score-Actor.ps1 -ActorProjectPath .agentlang/record-validation-help-adoption-002/project/actor -RunId actor-01
```

Raw inputs, traces, authored project, scoring copy, pin/freeze records, timing
and actor final statement are in the [evidence index](evidence/123-versioned-record-help/index.json).
Binary files are pinned by hash rather than duplicated in Git. Reused control
records remain in [report 122's evidence](evidence/122-record-validation-help-adoption/index.json).
No product source changed, so no new full-suite or native result is claimed.

Use explicit, verified frontend-version help in future experiments. This bounded
question is now sufficiently answered to return to comparative efficacy: an
unfamiliar rule repair or refactor with independent expectations and matched
foundation/helper contracts. [Report 124](124-efficacy-assessment.md) synthesizes
that decision and the remaining limits. The full PRD remains unfinished.
