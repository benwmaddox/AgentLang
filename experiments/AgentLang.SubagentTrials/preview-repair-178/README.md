# Preview repair study — preparation checkpoint 178

Status: controls calibrated; no participants dispatched. This is the first
defect-repair stage following report 177, not a completed efficacy comparison.
Freeze a distinct follow-up maintenance task before dispatching the sequence.

## Question and treatments

Can external coding agents repair a misleading, tested library function while
preserving its unrelated assertions and using established typed vocabulary?
The planned comparison has two fresh Luna/max participants per condition:
retained project state, reset-rich project state, and conventional F# workflow.
Rotate sequential dispatch order; do not infer a population reliability rate
from six participants on one sequence.

Retained and reset-rich starts come from report 171's final actor projects. Both
already contain preview implementations; the retained implementation calls the
earlier handoff abstraction, while reset-rich composes lower-level operations.
They also differ in tests, metadata and history. The treatment is the **whole
project-state package**, including prior correct history, not isolated function
retention. This is not a primitives-only condition. F# starts from the accepted
report-166 handoff project with a disclosed coordinator-authored preview wrapper
and matching basic preview tests. It is a workflow reference, not an identical
authorship history or a language-library qualification comparison.

## Introduced fault and permitted repair

The coordinator adds an early equality shortcut to preview: when replacement
expiry equals handoff time, return the original Store successfully before any
validation. This violates cancellation-first validation and positive replacement
duration. All inherited meaningful tests stay intact.

First check whether the unchanged attached tests detect the fault or whether the
library gate rejects missing branch coverage. Then add one deliberately mistaken
`zero-period-no-op` expectation asserting success for a zero-length replacement.
This is a controlled faulty implementation plus faulty test, not an assertion
that a prior agent introduced the defect. The faulty library control must pass
its complete qualification gate through supported dictionary operations;
otherwise this proposed trial is not ready. Do not change manifests manually,
drop library status, replace identity, or weaken a gate to force preparation.

The eventual public task must explicitly permit correcting that one mistaken
expectation and the preview implementation. Preserve every other existing test
and example, unrelated function/type, signature and complete input Store.
Separate independent behavior, collateral preservation, test repair, library
qualification and task finalization. Newly authored duplicate regressions are
not required when retained assertions adequately cover the contract.

## Acceptance and calibration

`cases.json` has 37 independently modeled fixtures: 33 historical cases and four
new zero-duration combinations with missing old subscription, cancelled old
subscription, before-start cancellation, and duplicate ID plus blank term.
There are 30 valid-reference fixtures and seven separately classified defensive
orphan-reference fixtures. Preview success returns the complete original Store;
errors retain cancellation, duplicate, term and period precedence.

`score_preview.py` reuses the existing Flow encoder and F# fixture decoder and
scoring project builder. It invokes saved candidate source without repairs or
candidate self-tests. F# scorer projects rebuild fresh; the foundation is freshly
built from the seed's Reference source. Correct controls must execute and pass
37/37. Faulty controls must execute all 37 with no setup errors and fail the
zero-period cases. Source preservation and library gates are assessed separately.
The oracle compares error codes and full projected state/input; it does not
exhaustively verify error message text. Existing attached exact-error assertions
remain an additional endpoint, not a substitute for that missing oracle check.

Example local scoring commands from the repository root:

```powershell
python experiments/AgentLang.SubagentTrials/preview-repair-178/score_preview.py --arm flow --project <saved-project> --cli <fresh-cli-dll> --label <unique-label>
python experiments/AgentLang.SubagentTrials/preview-repair-178/score_preview.py --arm fsharp --project <saved-project> --label <unique-label>
```

Exit zero means scoring setup completed, not behavioral acceptance. Inspect
executed counts, failures and setup errors in the saved JSON. Evidence writes
under `.agentlang/efficacy-maintenance-178/oracle`; use unique labels. These local
controls perform no application email/network operations and do not expand runtime effects.
Broker restrictions are workflow controls, not a cybersecurity certification.

## Dispatch gate

Before dispatch, freeze runtime/source hashes, all seed files, public prompts,
allowed operations, semantic acceptance and preservation exceptions; require
calibrated controls and successful broker smoke checks. Use the existing V2
broker with isolated trial projects, virtual effects and a 100-exchange limit.
Keep source access comparable and disclose patch guards and unequal seed suites.
Authoritative model tokens and fixed effective context limits are unavailable;
record protocol exchanges/bytes separately and leave unavailable values unknown.
Never restart a live broker because an observation times out.
