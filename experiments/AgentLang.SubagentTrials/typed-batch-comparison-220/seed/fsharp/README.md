# Conventional F# typed-batch seed

This is the fresh conventional baseline for comparison 220. `Domain.fs`,
`Fixtures.fs`, and the low-level subscription aliases in `Operations.fs` follow
the pure report-163 F# seed and compile against the current
`AgentLang.Business` source project.

The seed documents and tests `renewMonthly`, `renewAnnual`, and
`renewBatch(Store, SubscriptionRenewal list, Instant) -> Result<Store,
DomainError>`. Batch changes run in input order at one shared instant, preserve
the first failure, and return no partial Store. Its four batch behavior cases
and two batch-step helper cases mirror the accepted retained 213 scenarios;
the eight monthly/annual scenarios retain their inherited success and error
coverage.

The conventional API's `DomainError` is a small `{ Code; Message }` record.
Only the renewal wrapper maps native `AgentLang.Business.Domain.DomainError`
values into the nine code/message pairs frozen by the independent 216 model.
The underlying `Store`, `Subscription.start`, and `Subscription.cancel` APIs
remain native and unchanged. The scorer compares both fields exactly and does
not rewrite participant results.
