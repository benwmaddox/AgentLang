# Seed task (not dispatched)

Add a reusable operation that returns how much a customer has paid across their
invoices. Work in the supplied typed business environment and inspect its existing
operations before implementing new logic.

Public operation:

`customer.paid-total(store: Store, customer-id: CustomerId) -> Result<Money, BusinessError>`

The customer must exist. Return `CUSTOMER_NOT_FOUND` before doing aggregation if
the identifier is unknown. For a known customer, sum payments linked to invoices
owned by that customer. Count no other customer's payments and do not treat an
unpaid invoice's total as a payment. With no matching payments, return zero.
Use exact checked Int64 minor-unit arithmetic and return `MONEY_OVERFLOW` if the
sum does not fit. Preserve an earlier error while processing later entries.
Do not fail a queried customer's total because another customer's payments would
overflow if summed. The operation is pure and must preserve the input Store.

Normal input stores satisfy the supplied business domain invariants: unique
identifiers, existing owners/invoices, and one positive full payment per paid
invoice. Multiple payments for a customer belong to separate invoices. Nominal
Money values are signed Int64, but valid payment amounts in this task are positive.
Any defensive tests outside these invariants must be labeled as such, not
presented as reachable normal application states.

Use the current `fn`/property/`==` syntax (Flow/2) for new definitions. Document
the function and add meaningful tests, including ownership, empty/no-payment,
unknown-customer and overflow behavior. Preserve inherited definitions and tests.
Use existing typed helpers where appropriate. Publish new reusable functions as
library functions only if all actual library gates pass; do not weaken the gates
or claim qualification that was refused. Finish the task explicitly and report
actual tests, edits, reuse, errors and limitations.

Dispatch must add the frozen broker command, isolated project path, Flow/2 help
primer and access restrictions. This file alone does not authorize or record a
participant run. The seed participant must not receive the comparison task,
independent oracle, coordinator discussion, future summaries or other solutions.
