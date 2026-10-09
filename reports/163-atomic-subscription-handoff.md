# Atomic subscription handoff: fresh agent comparison

Both fresh agents produced correct implementations and reused the retained
cancellation and creation operations. Both saved submissions pass all 33
independent cases. This is positive evidence that the typed dictionary supports
discovery and composition on this task, but it shows no correctness advantage
over conventional F#. AgentLang's broker session took longer in this pair.

## Task and matched starts

Implement an atomic subscription handoff: cancel the old subscription at a given
instant, then create its replacement for the same customer and product. Preserve
the existing transitions' error precedence, unrelated entities, all unchanged
old fields, and the caller's original Store. Failed replacement creation must
return its error, never a successful intermediate cancelled Store.

One fresh Luna/max subagent per arm ran sequentially, without inherited thread
history, through the same bounded broker (100 exchanges maximum). Both prompts
encouraged useful reuse without naming the transition helpers. AgentLang had to
publish its new function as library and commit its task; F# edited Operations.fs
and SelfTests.fs and ran local validation. These are different authoring
workflows under the same behavioral contract.

The current runtime was built and validated at commit `7334500` by the local
37-check Release gate recorded in report 162. The refreshed AgentLang seed had
214 library definitions and 137 passing tests; F# had six baseline test groups.
Neither seed contained handoff. F# transitions return `(Store, Subscription)`;
AgentLang transitions return Store. F# therefore needs a result projection, while
AgentLang retrieves the old subscription before calling the transitions.

The provisional seed preparation is stored in the directory named
`subscription-handoff-161`; this report uses the refreshed report-163 workspace.
Before dispatch, review identified missing conventional library source access.
A frozen, uncompiled Reference/Business.fs was added inside the conventional
project so its broker could expose retained implementations, like language
introspection. Only Operations.fs and SelfTests.fs were authorized for editing.
Those file restrictions and prohibition of sibling/oracle inspection are
prompt-level, not operating-system isolation. Both participants report only
prompt reading and broker launch outside the broker.

## Independent outcomes

The independent specification enumerates occupied discrete time cells rather
than calling either production overlap implementation. Cases exercise exact
handoff boundaries, empty/cancelled occupancy, expired old subscriptions,
third-party conflicts, separate customer/product scopes, invalid replacement
inputs, and combined failures that distinguish validation order. Runtime
adapters check canonical complete entity projections and the original input.
AgentLang additionally checks subscription list order.

| Observation | AgentLang | Conventional F# |
| --- | --- | --- |
| Independent behavioral cases | 33/33 | 33/33 |
| Common valid-reference cases | 26/26 | 26/26 |
| Orphan-reference robustness cases | 7/7 | 7/7 |
| Retained cancel/start reused | Yes | Yes |
| Existing implementation preserved | Yes | Yes |
| Own final validation | 5/5 target; 142/142 total tests | Existing checks plus six target invocations pass |
| Broker exchanges | 29 | 26 |
| Broker error responses | 2 | 1 |
| Broker session duration | 319.1 s | 185.5 s |
| Request payload UTF-8 bytes | 22,162 | 13,405 |
| Response payload UTF-8 bytes | 93,244 | 94,429 |

The seven orphan-reference rows are adversarial robustness checks. F#'s public
constructors cannot create these invalid Store states; the hidden scorer uses
reflection to remove temporary owner records after constructing subscriptions.
That fixture capability is not exposed to participants. Report these separately
from the 26 common-reference cases; do not treat them as ordinary F# inputs.

Durations cover broker start through close, excluding pre-launch prompt handling.
Protocol bytes are not model tokens; broker exchanges are not model turns. Both
termination audits verify host.close and host/runtime exit codes 0/0. The F#
participant's terminal polling reported a missing process handle after closure;
the authoritative trace confirms clean termination.

## What the agents built

The language agent discovers retained APIs and adds one library function:

```text
fn subscription.handoff(store: Store, oldId: SubscriptionId, replacementId: SubscriptionId, term: String, handoffAt: Instant, expiresAt: Instant) -> Result<Store, BusinessError> {
    doc "Cancel the old subscription and start its replacement atomically at the handoff instant."

    match .store.subscription(oldId, store) {
        some oldSubscription => {
            match subscription.cancel(store, oldId, handoffAt) {
                ok cancelledStore => subscription.start(cancelledStore, replacementId, oldSubscription.customer-id, oldSubscription.product-id, term, handoffAt, expiresAt)
                error cancellationError => result.error<Store, BusinessError>(cancellationError)
            }
        }
        none => subscription.cancel(store, oldId, handoffAt)
    }
}
```

Effects default to none. The leading dot on `.store.subscription` disambiguates
the qualified function from the local `store` parameter. The missing-old branch
delegates to existing cancellation so its established error is retained.

F# directly matches the existing cancellation result, receives the old
subscription with the updated Store, calls existing creation, and maps its
successful tuple to the final Store. Neither agent rebuilds overlap or validation
logic. This contrasts with report 159, where AgentLang inspected a safe I/O
helper but duplicated its implementation. Reuse here is voluntary, not a hidden
acceptance condition.

The language preservation audit confirms 47 existing authored word rows and
revisions, 29 type rows, and 254 source objects unchanged. Only handoff is added,
with five attached tests and library maturity. The manifest stays version 3;
this task does not use the newer test-file overlay format. F# preserves every
existing operation and original test body, adding the function and regressions;
all six other infrastructure/source/library files are unchanged.

## Testing and recovery

Final language tests cover 25/25 own executable instructions, 4/4 branch outcomes,
and both Result return tags. Publication and task commit succeed. Initial tests
passed only 1/5: four incorrectly expected returned Result.error values to be
runtime failures. The agent corrected the expectations to inspect error codes;
the function body itself was unchanged between those definitions. Initial finite
return evidence lacked error; final passing tests supply both tags. No premature
library commit was attempted, so this trial does not demonstrate a gate stopping
an attempted bad publication.

The language's two broker error responses are an initial blank JSON request and
an unknown Unit type lookup. Its four assertion failures are separate from those
protocol errors because test responses report results with `ok: true`. F# has one
rejected patch with an invalid expected SHA-256; it recovers and both validation
calls succeed. Neither submission has a behavioral failure in hidden acceptance.

This distinction between returned errors and runtime failures is an authoring
usability issue. Library qualification is useful evidence about execution paths,
but does not establish all business behavior or abstraction value; independent
checks remain necessary.

## Controls and provenance

Before dispatch, both composed controls pass all 33 actual-runtime cases. Each
arm executes six faulty implementations and receives behavioral rejections for
duplicate validation before cancellation, leaking intermediate cancelled state,
creation before cancellation, ignored overlap, and customer-only/product-only
conflict scope. Fault implementations are not identical between languages:
creation-before-cancellation fails 12 AgentLang cases and eight F# cases. These
are scorer calibration, not comparative agent outcomes.

Both saved-submission scorer modes are also tested before dispatch: a saved
correct implementation passes 33/33, and a saved faulty implementation executes
all cases and is rejected. AgentLang's mode defines only an observation wrapper,
not the participant target. F# compiles the selected saved Operations.fs and calls
its target directly. Hidden scoring uses retained disposable copies and never
changes submitted source. Setup failures and corrections are retained separately
from behavioral rejections.

The pre-dispatch archive freezes and verifies 2,988 artifacts, including both
initial actor projects, prompts, runtime trees, independent model, scorers,
control outputs and source. Its SHA-256 is
`08bdcb370d2e59a7b46274d14e78e4f718928c1d196c09380295b96b646385c7`.
All prior failed setup attempts remain retained; no frozen trial evidence was
deleted or compacted. The final archive also retains submissions, traces,
acceptance outputs, preservation checks and post-trial audit scripts.

## Interpretation and next step

This pair supports the feasibility of an unfamiliar agent discovering and
composing tested domain functions through the dictionary. It does not show that
the dictionary outperforms ordinary source discovery: F# also reuses the same
transitions correctly, with a shorter broker session. One task per condition
cannot estimate reliability rates or establish a causal vocabulary-retention
advantage. Model token usage was not captured. Native performance, mailbox
isolation, arena behavior and LLVM backends are outside this interpreter trial.

Keep reliable edits and discovery/reuse as the primary research questions. The
next useful efficacy task should maintain or change an existing abstraction and
its callers under a restricted context, rather than accumulate more simple
composition demonstrations. Retain independent correctness oracles, library
gates and the conventional source-access baseline. Improve Result test examples
before the next trial; do not infer a missing language feature from test-authoring
mistakes alone.

Local validation only; CI remains manual-only. No production runtime semantics
changed for this experiment.

The [evidence manifest](evidence/163-atomic-subscription-handoff/archive.json)
indexes the verified archive: 4,109 entries, 16,177,815 bytes, SHA-256
`a0b03783029196c33739d1acfbaf254c1aa9c2f73c304bdd9324e6f54281bbb9`.
Post-trial verification confirms the pre-dispatch archive and 2,422 static input
files unchanged. Generated grading outputs are recorded separately from frozen
inputs. Final source is available in the [AgentLang submission](../experiments/AgentLang.SubagentTrials/subscription-handoff-161/submissions/agentlang-handoff.agent)
and [F# submission](../experiments/AgentLang.SubagentTrials/subscription-handoff-161/submissions/Operations.fs).
