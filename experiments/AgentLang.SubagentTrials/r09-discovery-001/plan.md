# R09-inspired refactor: shared paid totals in two public summaries

Status: preparation; no seed or comparison participant has run. Starting repository
revision: `6aa035a`. This is an exploratory reliability/discovery study, not a
throughput benchmark or a statistical superiority test. This adapts R09; it is
not completion of the task-bank's fixed-symbol contract.

Report 125 established correct edits and actual vocabulary reuse in all three
conditions, without a reliability lead over F#. This study changes domain and
requires two public summaries to remain consistent, including checked aggregate
arithmetic and preservation of unrelated data. Do not resume retention-004.

## Smallest implementation

Reuse the existing six-file business foundation, its nominal values, immutable
Store, lookup functions, checked Money operations and existing broker. Load its
existing definitions as Flow/1; new participant definitions use Flow/2. Do not
add compiler features, a generic experiment framework or a new model integration.
Use local subagents as participants. Preserve the existing library gates.

Prepare these contracts in both languages:

- `customer.paid-total(Store, CustomerId) -> Result<Money, BusinessError>`:
  reject an unknown customer with `CUSTOMER_NOT_FOUND`; otherwise sum payments
  for invoices owned by that customer, using checked Int64 minor units. Return
  zero for a known customer with no payments. Return `MONEY_OVERFLOW` on overflow.
- `customer.account-summary(Store, CustomerId)` returns the complete original
  Customer record and its paid total, or propagates the relevant error.
- `store.customer-metrics(Store)` returns the seven existing F# Store.summary
  counts (customers, products, subscriptions, invoices, payments, pending emails,
  sent emails) and the checked paid total across all customers. An empty store
  returns seven zero counts and zero money. Errors propagate, including overflow
  in either an individual customer's total or the dashboard aggregate.

The seed task names `customer.paid-total`. In the main comparison, accept any
shared function name with that typed contract. The public prompt intentionally
does not reveal the retained helper name, so the scorer must not impose a hidden
spelling requirement. The two summaries must share one customer aggregation
implementation. Account calls that operation; dashboard calls it for each customer
through its fold callback and combines results with checked arithmetic. Supporting
typed callbacks are allowed; duplicate ownership/filter/sum algorithms in the
two callers are not. A wrapper around two duplicated algorithms is insufficient.
Public output
record names are `CustomerAccountSummary { customer: Customer; paid-total: Money }`
and `StoreCustomerMetrics { customers: Int; products: Int; subscriptions: Int;
invoices: Int; payments: Int; pending-emails: Int; sent-emails: Int; paid-total: Money }`.
F# uses the corresponding PascalCase fields. Direct total/account calls look up
the customer before aggregation; an unrelated overflow cannot replace
CUSTOMER_NOT_FOUND. Dashboard totals all stored customers, independent of the
account query. Preserve input Store and unrelated definitions/tests.

Normal acceptance states must be constructible through the F# domain operations:
each successful payment covers one invoice's full amount and marks it paid.
Use separate invoices for multiple payments. The old proposed R09 vector's two
partial payments against one invoice do not satisfy that application contract.
Do not silently count such a vector as reference parity. Duplicate IDs, orphaned
relationships and negative payment amounts are outside this study's normal-state
contract; label any defensive coverage tests separately.

## Oracle and controls before participants

Freeze independently enumerated expected values before the seed participant:

1. Two owned paid invoices and another customer's paid invoice.
2. Known customer with no invoices.
3. Known customer with an unpaid invoice.
4. Only another customer has a payment.
5. Account summary preserves every customer field, and dashboard preserves all
   seven nontrivial counts, including outbox and sent messages.
6. Unknown queried customer; dashboard still describes the valid Store.
7. One maximum Int64 payment succeeds.
8. One customer's two invoices overflow when summed.
9. Another customer's overflow does not corrupt a queried customer's valid total;
   the dashboard still fails rather than suppressing that overflow.
10. Individual customer totals fit but their dashboard aggregate overflows.
11. Empty Store metrics are all zero; a direct query is CUSTOMER_NOT_FOUND.
12. An overflow followed by a further payment remains an error; a fold must not
    replace an earlier failure with a later partial total.
13. Unknown queried customer with an overflowing known customer: direct/account
    still return CUSTOMER_NOT_FOUND while dashboard returns MONEY_OVERFLOW.

Pin ordered payment IDs and verify enumeration order before claiming sticky-error
coverage. Construct extra state by starting one subscription, queuing two distinct
messages and delivering the first. Freeze exact mapping/dates/text with adapters.

Each applicable case checks the direct total and both summaries, not just the
helper. Compare whole original Customer values, complete Store projections and
all metrics counts. Check stable error codes; document message normalization.
Implement one narrow scorer, using existing broker and reference fixture tools.
Before dispatch require correct controls in both languages and rejection of
wrong-owner and unchecked-overflow mutations on copies. Starting suites must pass.
No oracle or scoring edits after dispatch; a necessary repair invalidates that
run for the frozen comparison and must be reported.

## Seed and comparison

First give a separate fresh agent the paid-total task only. Independently accept
its implementation, attached tests, reload and maturity before using its exact
output as retained vocabulary. Retain its source and interaction trace. This
agent is not a comparison replica. A scripted correct control is not an
agent-created seed. If no acceptable seed exists, report that outcome; do not
quietly substitute a hand-authored helper and keep the retention claim.

Create three starts with matched business foundations and equivalent, initially
correct but duplicated summary implementations: retained Flow/2 with the accepted
seed; reset-rich Flow/2 with
the same foundation and no seed; and conventional F# with an accepted equivalent
helper. Document helper test/topology differences. Do not pad dictionaries with
artificial distractors to equalize raw entry counts. Reset-rich means the same
substantial business foundation, not exactly the same total word count.

The same public comparison prompt describes the summary requirements without
naming or locating the optional helper. Keep helper structural checks in scorer
instructions, not discovery hints. Agents may inspect existing capabilities and
create their own shared helper. Report actual dependency/source reuse separately
from correct outputs. Flow agents must finalize their task explicitly.

Run one fresh Luna/max actor per arm, without inherited conversation or coaching.
If all three pass behavior and preservation, stop and report another ceiling
feasibility result. If outcomes differ, run one additional fresh replica per arm
from the same frozen starts, prompt and oracle. This outcome-dependent exploratory
replication rule precludes pooled confirmatory/statistical inference. Report every
attempt, including seed failure, publication failure and protocol failure.

All starting summaries must pass the thirteen behavioral scenarios; the initial
failure is duplicated implementation, not a planted behavior defect. Main scoring
records the actor's selected shared symbol, checks its signature and dependency
paths, and runs direct helper cases against that symbol. Retained seed identity
reuse is a separate measure. Primary outcome: shared structure with independently
correct summaries and unrelated behavior preserved. This does not measure repair
of initially wrong behavior. Also record helper discovery/use, own-test regression detection,
library qualification, task finalization and error recovery. Payload bytes and
broker exchanges are descriptive, not model tokens or equivalent work units.
Do not infer native runtime performance from these interpreter trials.
