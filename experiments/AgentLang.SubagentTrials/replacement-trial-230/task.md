# Replace monthly renewal timing with one nominal value

Both arms start from fresh copies of the accepted scheduled-renewal endpoints.
The monthly renewal operation currently receives a store, old and new
subscription IDs, a start/cancellation instant, and a replacement expiry.
Group the two instants in a new nominal `RenewalTiming` record and migrate the
existing monthly operation and its production callers to that input shape.

In AgentLang, change `subscription.renew-monthly` from
`(store, oldId, newId, at, expiry)` to `(store, oldId, newId, timing)`.
In F#, change `Subscription.renewMonthly store oldId newId at expiry` to
`Subscription.renewMonthly store oldId newId timing`.

The record contains `At` and `ExpiresAt` in F#, and `at` and `expires-at` in
AgentLang. Preserve the existing operation identity and behavior. `At` remains
the old subscription's cancellation instant and the new subscription's start
instant. `ExpiresAt` remains the new subscription's expiry. Keep the same
store handling, errors, and input preservation.

Add and persist the AgentLang record first. Then replace
`subscription.renew-monthly`, `subscription.renew-batch-step`, and
`subscription.renew-scheduled-batch-step` together, using `replace: true` and
the exact expected revisions observed in the live session. Keep the operation
identity and advance its revision. Do not add helper definitions or packages.
Use Flow/2 for every AgentLang `define` and `eval` request, with
`frontend: "flow"` and `syntaxVersion: 2`.

The AgentLang production callers are the common batch step and the scheduled
batch step. The F# production caller is `renewBatchStep`, which is used by both
`renewBatch` and `renewScheduledBatch`. Keep public batch signatures, supplied
order, per-item times, first-error behavior, atomic failure, complete Store
contents, and unrelated semantics unchanged. Annual renewal is unchanged.

In AgentLang, update every direct monthly test call to construct
`RenewalTiming`, preserving its current cases and expectations, and add one
direct grouped-time regression. In F#, edit only `Operations.fs` and
`SelfTests.fs`; adapt the existing monthly scenario callback to construct the
record, retain the annual scenarios and assertions, and add one direct
grouped-time regression. Keep `SubscriptionBatch.Conventional.fsproj` as the
fixed validation project. All other F# seed files must remain byte-identical.

The matched behavior contract contains the seven existing scheduled-renewal
cases plus two direct monthly cases. The direct cases cover a valid grouped
time with exact replacement and preserved unrelated Store/input state, and
`ExpiresAt == At` returning the existing exact expiry error without changing
the input. Compare complete canonical Store projections. Preserve the
AgentLang inherited regression, library qualification, and unrelated
dictionary evidence as a separate gate; it is not an equivalent F# case count.

This is one feasibility observation. Report the inherited architecture
difference: F# routes both batch paths through `renewBatchStep`, while
AgentLang has separate common and scheduled step callers. Do not attribute
that difference to the language.
