# Conventional F# start (coordinator preparation)

This is an isolated copy of `experiments/AgentLang.Business` and its console
test project. The two projects compile only the copied `Business.fs`; their
project reference does not resolve to the canonical library.

The public names used in this preparatory copy are:

- `Customer.accountSummary : Store -> CustomerId -> Result<CustomerAccountSummary, DomainError>`
- `Store.customerMetrics : Store -> Result<StoreCustomerMetrics, DomainError>`
- `Store.customerPaidTotal : Store -> CustomerId -> Result<Money, DomainError>`
- `Store.customerPaidTotalStep : CustomerPaidTotalState -> Payment -> CustomerPaidTotalState`

`CustomerAccountSummary` carries `Customer` and `PaidTotal`. The dashboard
record has `Customers`, `Products`, `Subscriptions`, `Invoices`, `Payments`,
`PendingEmails`, `SentEmails`, and `PaidTotal`. Both summary functions are
initially correct, separately implemented checked aggregations and intentionally
do not call the discovered Store.customerPaidTotal helper. The helper and
CustomerPaidTotalState mirror the accepted fold seam and remain unused until
the actor refactors both summaries. The root's final comparison contract
remains authoritative if it changes names before participant dispatch.

`Store.customers` and `Store.payments` return typed snapshots from the private
maps without exposing or changing the `Store` representation. Lists follow
F# `Map` key order. For the bounded R09 fixtures, numeric symbols map to GUIDs
whose kind prefix is `10000000` for customers, `20000000` for invoices,
`30000000` for payments, `40000000` for products, and `50000000` for
subscriptions, with the numeric suffix as the final twelve digits. This keeps
payment map order aligned with the fixed invoice/payment recipe order; it does
not assert general insertion-order parity with Flow.

The copied test executable retains the original seven Business test groups,
adds one public summary smoke group, and has nine focused tests for the helper.
The missing-invoice and pre-existing-error step tests are labeled standalone
defensive state tests, since valid public workflows do not construct those
states. The full 13-case oracle corpus is kept only in
`.agentlang/r09-discovery-001/`.

The coordinator-only replay utility and validation evidence are under
`.agentlang/r09-discovery-001/conventional-preparation/`.
