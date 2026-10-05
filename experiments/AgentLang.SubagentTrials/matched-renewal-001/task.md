# Shared task prompt

Implement a reusable renewal-balance operation. Start with the customer's
existing baseline discounted balance: a customer whose kind is exactly
`premium` receives a 10% discount; every other kind keeps the original
balance. Apply a further 5% reduction to that already-discounted balance only
when the subscription term is exactly `annual` and `renewable` is `true`. In
all other cases, return the baseline discounted balance unchanged.

Expose the result as `customer.renewal-balance : Customer Subscription ->
Float` in the language fixture, or `Renewal.balance` in the F# fixture. Inspect
the environment and discover the available operations rather than assuming a
preloaded helper list. Add library-quality tests that exercise the relevant
conditional outcomes; check all eight premium/non-premium, annual/monthly,
renewable/non-renewable combinations. Run the tests and report what changed.

The language fixtures represent premium status, term, renewable status, and
balance as raw `String`, `Bool`, and toy `Float` values. Do not treat this
fixture as the stricter Money/Email/ID/time domain contract.
