# Signed renewal balance repair

Renewal calculations are wrong for customers with a credit (negative balance).
The balance must remain signed. For kind exactly `premium`, first multiply by
0.9; otherwise keep the balance. Then apply another factor of 0.95 when the
subscription term is exactly `annual` and renewable is true, including standard
customers. For example, a premium customer with balance -100 and an annual
renewable subscription must produce -85.5.

Inspect the dependency chain of `customer.renewal-balance : Customer Subscription
-> Float` and repair the shared cause so its existing callers inherit the fix.
Preserve the renewal word's implementation, all type declarations and unrelated
helpers. Correct any mistaken self-test expectation, retain existing test names
and all valid cases, and add regression tests for signed credit behavior.
Keep the repaired shared word pure and retain it as a tested library word.
Commit the task durably. Do not add a parallel workaround or duplicate the logic.
