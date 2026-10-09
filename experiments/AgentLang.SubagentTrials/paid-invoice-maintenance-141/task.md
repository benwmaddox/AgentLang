# Paid-invoice total maintenance task

The customer account summary and store dashboard already share a typed
customer payment-total operation. Update the shared aggregation so it counts a
payment only when its linked invoice exists, belongs to that customer, and has
status `Paid`. Keep both summary paths using the same shared operation.

The shared operation's public contract is:

```text
Store, CustomerId -> Result<Money, BusinessError>
```

Discover its existing name and implementation from the assigned project. Do not
change that shared operation's public name or signature; you may revise or add
internal supporting callbacks. Do not change public summary signatures or
record shapes:

- `customer.account-summary(Store, CustomerId)` returns the complete original
  customer and the paid total.
- `store.customer-metrics(Store)` preserves all seven existing counts and
  returns the sum of all customer paid totals.

Preserve unknown-customer precedence, checked signed Int64 minor-unit
arithmetic, sticky earlier errors, purity and unrelated behavior. A missing
invoice contributes nothing. A payment linked to an open invoice contributes
nothing. Once a linked invoice is `Paid` and owned by the queried customer,
continue to apply the existing checked addition behavior to its signed amount;
do not add a positivity rule.

The assigned project includes these coordinator-owned fixture helpers for
tests:

- Flow/2: `maintenance141.fixture-import-payment(Store, PaymentId, InvoiceId,
  CustomerId, Option<InvoiceStatus>, Money) -> Store`. Use
  `InvoiceStatus::new("paid")` or `InvoiceStatus::new("open")`; `none` omits
  the invoice record.
- F#: `Store.Maintenance141Fixture.importPayment(Store, InvoiceId,
  CustomerId, InvoiceStatus option, PaymentId, Money) -> Store`. Use `Some
  Paid`, `Some Open`, or `None` to omit the invoice record.

Each call adds one Payment and, when a status is supplied, one matching Invoice
row. Use the helper only in tests to construct imported rows, including missing
or open invoice links and signed amounts. Do not edit either fixture helper or
call it from production definitions. Imported malformed rows are defensive
cases outside the normal business invariants.

Add focused regression tests for the changed rule and preserved behavior. They
must cover the direct shared operation and both summary callers, open and
missing invoice links, signed paid amounts, checked overflow, and unknown
customer precedence. Keep all inherited definitions and meaningful tests, and
do not weaken any library gate. Run the complete attached test suite through
the assigned validation command. For Flow, qualify the shared operation and
its required support functions under the existing gates. Save your changes.
For Flow, explicitly commit the task and close the host cleanly.

Report the discovered operation, what changed and reused, tests and
qualification results, finalization, and any errors or remaining limitations.
