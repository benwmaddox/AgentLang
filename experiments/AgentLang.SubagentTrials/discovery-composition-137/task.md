# Held-out discovery and composition task

Add this Flow/2 operation to the supplied project:

`customer.remaining-lifetime-allowance(store: Store, customer-id: CustomerId, cap: Money) -> Result<Money, BusinessError>`

The cap and paid total are signed Int64 minor-unit Money values. Return the mathematical value `max(0, cap - paid-total)`. If the positive difference is greater than `Int64.MaxValue`, return `MONEY_OVERFLOW`. A negative difference is clamped to zero. Preserve the complete BusinessError returned by the paid-total operation, including when `cap` is zero or negative; resolve that operation before any clamp that could otherwise hide its error.

The nominal `Payment.amount` field is signed Money, so a well-typed Store can contain negative payment records even though normal payment workflows require positive amounts. Apply the stated mathematical rule to every well-typed Store; do not assume the paid total is nonnegative.

The project already has a reusable library operation with this contract:

`(Store, CustomerId) -> Result<Money, BusinessError>`

For an unknown customer it returns `CUSTOMER_NOT_FOUND` before aggregation. For a known customer it returns the checked Int64 sum of recorded payments on that customer's invoices, returns zero when there are none, and preserves a `MONEY_OVERFLOW` error. Find and call the existing operation. Do not repeat its invoice ownership lookup, payment filtering, or aggregation in this new operation.

Before authoring, inspect both current Flow/2 help responses by sending these requests:

```json
{"op":"help","syntaxVersion":2}
{"op":"help","topic":"define","syntaxVersion":2}
```

Use a `fn` definition with attached documentation and meaningful attached tests. The new operation must be pure, qualify as a library function under the existing gates, and preserve the supplied Store. Preserve all inherited types, functions, tests, and fixtures. Do not change the library gates. Run the relevant tests, commit the function as a library only after its gates pass, and finish the task explicitly. Report the actual changes, tests, helper reuse, any errors, and any remaining limitations.
