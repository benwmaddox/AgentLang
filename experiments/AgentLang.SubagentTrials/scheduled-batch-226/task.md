# Scheduled renewal batch

Add one new API to the assigned project:

- AgentLang: define `ScheduledSubscriptionRenewal` with `renewal:
  SubscriptionRenewal` and `at: Instant`, then add
  `subscription.renew-scheduled-batch(Store, List<ScheduledSubscriptionRenewal>)`.
- F#: define the corresponding record with `Renewal: SubscriptionRenewal` and
  `At: Instant`, then add `Subscription.renewScheduledBatch`.

The result is a complete `Store` on success or the exact existing business
error on failure. Process items in the order supplied. Use each item's `at` for
that item's renewal. Stop at the first error. Do not return partial Store state
as success. An empty list succeeds with the entire input Store. Preserve
complete Store contents, unrelated entities and state, and the caller's input.

Keep every existing definition, test, example, fixture, and expected result
unchanged. Make additive changes only. In F#, edit only `Operations.fs` and
`SelfTests.fs`; add regression tests for per-item instants, input ordering,
first-error behavior, failure after a successful prefix, complete Store
preservation, and empty input. Keep the model pure: do not add I/O, packages,
providers, mutable global state, or project changes.
