# Existing handoff signature maintenance

Status: both fresh participants completed; each saved implementation passes
132 independent behavior checks. Preservation review is complete: language
library qualification passes, but two prior test scenarios lose evidence;
conventional prior assertions are preserved. See report 166. At dispatch,
the focused runtime suite passed 1,507 assertions; the full gate
was still running its older business-policy preflight. Trials ran against the
pinned runtime while that check continued. The frozen inventory records the
pending gate at dispatch. The complete 37-check local gate subsequently passed
before committing the milestone. The Flow
seed has 217 library words and 146 passing attached tests; the
conventional seed builds and passes its retained self-tests. Both correct controls
pass all 66 target checks. Both executing faults are rejected on dry-run behavior
(16 skip-validation failures and 10 modified-store failures in each environment).
Both saved-submission graders also pass calibration for the retained callers,
33 fixtures each. Flow preserves target identity and persistent state; the
conventional grader preserves submitted source. Controls are behavioral calibration only;
Flow controls use project maturity and the pre-handoff domain, while participants
start with the actual accepted library implementation from report 163.

Question: can a fresh coding agent change an existing tested abstraction and its
callers while preserving validation behavior and unrelated project state?
This tests coordinated maintenance rather than adding another composition.

Reuse the actual accepted handoff implementations from report 163 in their
respective languages. Add two coordinator-authored, documented library callers:
`subscription.renew-annual` and `subscription.renew-monthly` (equivalent F# names).
They call handoff with the corresponding term and otherwise forward arguments.
Disclose this preparation; do not call these wrappers prior agent-created work.
Keep the ordinary business foundation and source-access parity from report 163.

## Task behavior

Add a trailing Bool `dryRun` parameter to the existing handoff function, retaining
its name and identity. All cancellation and replacement-creation validation must
run in its established order for both values. On successful validation, false
returns the updated store and true returns the original store. Both modes return
the same existing error on failure. A dry run must not skip creation validation.
Update the two retained callers and existing target tests to pass false so their
behavior remains unchanged. Add meaningful tests for the new mode, including a
case that is rejected only by replacement creation. Preserve unrelated source
and tests. In AgentLang, preserve library maturity and commit the coordinated
replacement through the public protocol; complete the task session normally.

This changes the public six-argument target to seven arguments. Do not accept a
renamed transitional helper as a substitute for the requested signature change.
Do not reveal implementation helpers or hidden case fixtures in the task primer.

## Minimal independent comparison

Extend report 163's bounded cell-model oracle; do not create a new research
platform. Run each of its 33 existing cases in both dry-run modes, with errors
unchanged and successful dry runs compared against the original complete store.
Keep the original split of 26 valid-reference and seven adversarial orphan-owner
cases explicit in the results. Run all 33 fixtures through each retained caller,
recomputing the independent expected result with its fixed annual/monthly term.
Report these 66 caller checks separately from the 66 target checks.
Compare input preservation and collateral state as before.

Before dispatch, verify a correct control and two executing faulty controls:
one skips replacement validation in dry-run mode, and one returns the modified
store for a successful dry run. The real graders must reject both faults.
Freeze the exact accepted seeds, prompt, oracle and runtime binaries. Use fresh
Luna/max subagents, the existing broker, equal exchange limits and sequential
execution. Participant-authored tests do not define acceptance. Keep source access
equivalent; this study does not claim a fixed token-context limit without a
measured token count. No mandatory helper reuse beyond preserving existing APIs.

Report hidden outcomes, own tests, library gates, retained IDs/revisions, source
preservation, compile/test recovery, broker exchanges/bytes and duration. Record
whether the agent discovers and uses coordinated staging. Preserve failed
attempts. A single pair can expose usability problems and establish bounded
feasibility; it cannot establish comparative reliability rates or causal benefit
from vocabulary retention.
