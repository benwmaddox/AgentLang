# Public refactoring task (not dispatched)

The customer account summary and store dashboard currently calculate customer
payment totals separately. Consolidate that duplicated business logic into one
reusable, typed customer payment-total operation, and have both summaries use it.
Inspect the existing project before deciding what to create or change.

Preserve these public contracts:

- `customer.account-summary(Store, CustomerId)` returns
  `Result<CustomerAccountSummary, BusinessError>`. Its successful record contains
  the complete original `customer: Customer` and `paid-total: Money`.
- `store.customer-metrics(Store)` returns
  `Result<StoreCustomerMetrics, BusinessError>`. Its successful record contains
  `customers`, `products`, `subscriptions`, `invoices`, `payments`,
  `pending-emails` and `sent-emails` as Int counts, plus `paid-total: Money`.
  Preserve the meaning of all seven existing store counts.

In the conventional F# project, use its corresponding existing public function
and PascalCase record field names. Keep existing public signatures and record
shapes unchanged in either environment.

A customer total counts only payments for invoices owned by that customer, not
unpaid invoice amounts or another customer's payments. A known customer with no
payments has total zero. An unknown customer produces `CUSTOMER_NOT_FOUND`
before aggregation. The dashboard sums the totals of all stored customers;
an empty store has zero counts and zero paid total.

Use checked Int64 minor-unit arithmetic throughout. Overflow must return
`MONEY_OVERFLOW`, including when individual customer totals fit but their
dashboard sum does not. Preserve an earlier error while processing later items.
Another customer's overflowing total must not affect a valid account query or
replace the unknown-customer error, but it must fail the all-customer dashboard.

Normal input stores satisfy the supplied business invariants: unique identifiers,
existing owners and invoices, and one positive full payment per paid invoice.
Multiple payments for one customer belong to separate invoices. Label any
defensive tests outside these invariants rather than treating them as normal
application states.

The resulting functions must remain pure and preserve the complete input Store,
customer data and unrelated project behavior. Add meaningful regression tests,
run the relevant existing tests and preserve inherited definitions and tests.
Use one shared customer aggregation implementation rather than retaining separate
ownership/filter/sum algorithms behind a wrapper. Supporting typed callbacks are
allowed. The name of the shared operation is your choice.

For language definitions, use current `fn`, property and `==` syntax with attached
documentation. Reusable functions must satisfy the actual library gates; do not
weaken those gates or claim qualification that was refused. Persist your changes
and explicitly finish the task. Report what you changed, reused and tested, plus
any errors, qualification failures or remaining limitations.

Coordinator note, excluded from participant dispatch: this task is not ready to
run until an accepted seed, three equivalent initially correct starts, complete
summary scoring, controls and exact per-arm prompts have been frozen. Do not
reveal the optional helper's name or location in the surrounding prompt.
