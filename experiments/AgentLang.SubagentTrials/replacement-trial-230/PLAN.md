# Replacement trial 230: group monthly renewal timing

Status: preparation in progress; no participants dispatched. Report 229 is accepted on
`main` at `74cc644` with 37 full checks and 1,008 native assertions passing.
Build and hash a fresh 230 runtime from that accepted revision; leave all 226
and 228 pins and evidence untouched.

## Objective and exact task

Run one fresh matched AgentLang/F# pair to test whether an existing function
signature can be replaced, actual callers migrated, and held-out behavior and
unrelated evidence preserved. Report 228 accepted both scheduled-renewal
participants, found fewer interactions and shorter broker time for F# in that
pair, and recommended definition replacement as the next task. This one pair
is a feasibility observation, not a reliability advantage claim.

Seed from fresh copies of the accepted 228 retained and F# endpoints. Add the
same nominal record in both arms:

```text
RenewalTiming { At: Instant, ExpiresAt: Instant }
```

Use AgentLang's field spelling `at`/`expires-at` and F#'s `At`/`ExpiresAt`.
Then replace the existing monthly helper's five-input call shape with four:

| Arm | Replace | Actual production callers |
| --- | --- | --- |
| AgentLang | `subscription.renew-monthly(store, oldId, newId, at, expiry)` → `subscription.renew-monthly(store, oldId, newId, timing: RenewalTiming)` | `subscription.renew-batch-step`; `subscription.renew-scheduled-batch-step` |
| F# | `Subscription.renewMonthly store oldId newId at expiry` → `Subscription.renewMonthly store oldId newId timing` in `Operations.fs` | `renewBatchStep`, reached by both `renewBatch` and `renewScheduledBatch` |

The replacement must map `At` to the old start/cancellation instant and
`ExpiresAt` to the replacement expiry, with the same Store, errors, and input
preservation as before. Annual renewal stays unchanged. Preserve all public
batch signatures, supplied order, per-item scheduled times, exact first-error
behavior, atomic failure, complete Store contents, and all unrelated semantics.
This is an input-shape change only.

The AgentLang replacement feature excludes new types from the replacement
batch. Add and persist `RenewalTiming` first, then atomically replace
`subscription.renew-monthly` and its two actual callers with `replace:true`
and exact `expectedRevisions`. Do not use a captured callback: current Flow
list callbacks cannot capture caller locals. The F# arm adds the type and
updates the function/caller in `Operations.fs`.

## Allowed edits and protocol

AgentLang uses the existing broker and Flow/2. Send `frontend:"flow"` and
`syntaxVersion:2` on `define` and `eval`. Update only the two production
callers and all direct `renew-monthly` test call sites to construct
`RenewalTiming`. Preserve those tests' cases and expectations. Add a direct
grouped-time monthly test. Keep the old function identity, advance its revision,
qualify it through its tests and caller/library gate, and preserve unrelated
dictionary identities, cases, examples, and references.

F# edits are limited to `Operations.fs` and `SelfTests.fs`. Keep
`SubscriptionBatch.Conventional.fsproj`; adapt the existing monthly scenario
callback to construct `RenewalTiming`, keep annual scenarios and assertions
unchanged, and add the same grouped-time test. All other seed files remain
byte-identical; add no packages.

Use fresh actor copies and the existing interactive broker protocol: one live
handle per arm, `tty:true`, closure through that handle. Launch F# first to
reverse report 228's order. Keep the task broker-only, with no web or external
effects. The public-call contract is matched; disclose that F# routes both
batch paths through `renewBatchStep` while AgentLang has separate common and
scheduled step callers. Do not interpret that architecture difference as a
language effect.

## Acceptance, preservation, and controls

Use the existing seven 228 scheduled cases unchanged in both arms, and add two
held-out direct cases for `renew-monthly`/`renewMonthly`:

1. A valid grouped timing replaces the old subscription at `At`, starts the
   new one at `At`, sets its exact expiry to `ExpiresAt`, preserves unrelated
   Store contents, and leaves the input unchanged.
2. `ExpiresAt == At` returns the existing exact expiry error and preserves the
   input Store.

Compare complete canonical Store projections. Report the nine common cases.
The F# independent build excludes participant `SelfTests.fs`, as in report 228.
Keep AgentLang's inherited 66-case regression, library qualification, and
preservation evidence separate; do not describe it as 66 independent F# cases.
Adapt only the monthly call construction in the inherited AgentLang oracle;
keep its expected behavior and all other model cases unchanged.

Preservation is a separate gate. AgentLang's only allowed dictionary delta is
the new `RenewalTiming` type, replacement of `subscription.renew-monthly`, its
two direct caller bodies, and the arity updates to direct test calls. Preserve
all other IDs, definitions, tests, examples, and source references. F# may
change only `Operations.fs` and `SelfTests.fs`; old assertions remain equivalent
and other files remain byte-identical.

At storage level, allow only the new record's generated constructor/accessors
and the three replaced functions' advanced current revisions and appended
revision rows. Keep every inherited revision row and referenced source object
byte-identical. The aggregate `projectSource` pointer changes on publication;
it is not an immutable seed object pointer requirement. The six direct monthly
test invocations occur in four monthly test owners and the scheduled-step
`monthly-instant` test; update their call shapes only. For the expiry-equals-start
case, use a distinct new subscription ID so duplicate-ID precedence cannot
mask the intended expiry error.

Calibrate on disposable copies only: a correct replacement passes; swapping or
misrouting `At`/`ExpiresAt` fails the direct cases; an old-arity caller is
rejected before publication; and a weakened inherited assertion is rejected by
the preservation audit. Controls are not participant outcomes.

## Minimal preparation and validation

Reuse the 228 preparation and broker-launcher shape and the 226 scorer scripts
in the new 230 folder. Change only paths/runtime pins, the two direct monthly
cases, and the scoped metadata/preservation allowance. Snapshot both 228 seeds;
smoke the correct control and three faults; freeze after hashes and receipts
pass. After dispatch, run AgentLang `test-all` and publish through the library
gate, run F# `validate` on the fixed project, then score endpoint snapshots and
audit source hashes before/after scoring and broker closure.

Planned repository-root preflight commands after the 230 paths exist:

```powershell
pwsh -NoProfile -ExecutionPolicy Bypass -File .agentlang/replacement-trial-230/preflight/fsharp.ps1
pwsh -NoProfile -ExecutionPolicy Bypass -File .agentlang/replacement-trial-230/preflight/retained.ps1
python experiments/AgentLang.SubagentTrials/replacement-trial-230/oracle/score_language.py --help
python experiments/AgentLang.SubagentTrials/replacement-trial-230/oracle/fsharp/score_fsharp.py --help
```

Use the copied scorers with their 226-style project/runtime arguments and new
230 evidence roots. One pair cannot estimate success rates, causality, or cost
advantage; report exchanges, wire bytes, and broker time as protocol measures.
