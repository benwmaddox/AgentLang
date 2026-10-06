# Consolidate customer discount logic

Customer classification and discount calculation are duplicated in the balance
and renewal operations. Refactor the existing vocabulary so one existing
reusable predicate owns the exact premium check and renewal reuses the shared
discounted-balance operation. Inspect available words before editing; do not
create another predicate or a parallel discount implementation.

Preserve both public entry points and their signatures:
`customer.discounted-balance : Customer -> Float` and
`customer.renewal-balance : Customer Subscription -> Float`.
Premium means kind exactly `premium`; multiply its signed balance by 0.9.
Other kinds retain their signed balance. An exactly `annual`, renewable
subscription gets another factor of 0.95, including standard customers.
All comparisons are case-sensitive; zero and credit balances remain supported.

Keep stable identities, existing test names and test sources, record types,
and unrelated helpers unchanged. Run relevant tests with complete own
instruction/branch coverage for each revised pure library word, replace the
words through the protocol, and commit the task durably. No new word is needed.
