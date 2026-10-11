# F# independent source acceptance review — corrected PowerShell trial 220

Decision: accepted edit. No source-preservation or contract defect found. This decision covers the completed actor source and saved finalization evidence; it does not infer the other arm's outcome or a general reliability claim.

Reviewed read-only against `baselines/fsharp`: all six source/project files, the full `Domain.fs`, `Operations.fs`, and `SelfTests.fs` diff, `experiments/AgentLang.SubagentTrials/typed-batch-comparison-220/task.md` and `PLAN.md`, the frozen `packets/fsharp.md`, saved `traces/fsharp.jsonl`, and the completed conventional score/manifest. No actor execution, repair, new harness, CI, web access, or participant message was performed for this review. The only written file is this review.

## Scope and frozen interfaces

Only `Domain.fs`, `Operations.fs`, and `SelfTests.fs` differ from baseline, matching the packet's permitted source-only scope. `Fixtures.fs`, `Program.fs`, and `SubscriptionBatch.Conventional.fsproj` are byte-identical. The dependency/project contract is unchanged.

The Domain diff adds only the named F# record `SubscriptionRenewalFailure` with exactly `Index: int`, `OldId: SubscriptionId`, and `Cause: DomainError`. The nominal business `SubscriptionId` alias remains unchanged. The existing `DomainError` record, accessors, and every `fromNative` code/message mapping are unchanged, including the frozen overlap wording.

`SubscriptionPlan`, the `SubscriptionRenewal` input record, Store helpers, subscription projection helpers, `start`, `cancel`, `renewWithTerm`, `renewMonthly`, and `renewAnnual` are unchanged. Existing batch/helper names and Store/list/Instant batch inputs remain. Changes to the state record are the typed outcome and added integer counter.

## Implementation semantics

`renewBatch` initializes `Outcome = Ok store`, `At = at`, `Index = 0`, and folds the original input list in order. The successful step uses the exact Store returned by the unchanged monthly/annual renewal and increments the counter once. The failing step captures the current counter, that input's `OldId`, and the unchanged returned cause; it neither increments the counter nor exposes a Store in the error. An already failed step returns the complete state unchanged and executes no further renewal. Thus fold traversal continues, but domain processing stops at the first failure. Empty input returns the complete original Store, and no mutable input or partial-success path is introduced.

## Inherited evidence and regressions

All eight inherited monthly/annual scenarios are untouched: replacement/full Store success, missing old subscription, already-cancelled precedence, before-start cancellation, and replacement-creation error assertions remain in the shared runner invoked for both plans, with original fixture IDs/timestamps, success expectations, and input assertions.

All six inherited batch/helper scenarios are preserved: empty list, single annual item, monthly-to-annual chain, first failure after a successful prefix, missing-old helper step, and helper monthly/annual plan coverage with earlier-error preservation. All inherited labels, fixture inputs, exact underlying cause code/messages, full successful Store expectations, timestamp assertions, and input assertions remain. The diff adapts failure assertions to inspect `Cause` and adds index/old-ID checks; helper state constructors gain the counter and helper expected outcomes gain only a typed error wrapper. The artificial earlier-error payload retains its original `FIRST_BATCH_ERROR` code and message inside `Cause`; full state equality still verifies preservation.

The new `[monthly; missingAfterRepeatedOld]` regression exercises a genuine successful prefix and asserts index 1, the second item's missing ID, and exact not-found cause. The inherited `[monthly; repeatedOld; missingAfterRepeatedOld]` first-error scenario now also asserts index 1 and the repeated old ID, while retaining already-cancelled code/message. It distinguishes first-error localization from a later missing error and complements the separate later-item regression. No inherited assertion was removed or weakened.

## Saved validation and finalization

Trace exchange 28 records successful fixed-project validation using `dotnet run --project SubscriptionBatch.Conventional.fsproj --no-launch-profile -p:NuGetAudit=false`, exit code 0, no timeout or output truncation, empty stderr, and the unchanged suite success text. That text still counts the inherited eight monthly/annual and six batch/helper scenarios; the new regression is additional executed source coverage, not a newly reported test-count metric.

The saved trace ends with `host-close` at 28 exchanges and `session-end` termination kind `host-close`, host exit 0, runtime exit 0, zero runtime stderr bytes. This is normal broker finalization. The trace contains only the listed runtime operations and the transport close; it cannot itself prove absence of unrecorded non-broker participant activity. Broker confinement remains prompt-governed as specified in the plan.

The completed independent score at `outcomes/conventional/run-20261011T132036Z-01/score-summary.json` records all nine cases executed and passed, no setup failures, build/runner exit 0, and unchanged actor/scoring-copy hashes. Participant self-tests were excluded from the scorer build. This review read that receipt without rerunning or modifying scoring.

Reviewed actor SHA-256 values: Domain `2a697dac40192548a5c8ef451a217dcef9f1418a27492b314bc05c5217371fe3`; Operations `94be08526847b0fd6175b6e0b0847fee5adee0456f5966dc26958b7be3b80095`; SelfTests `59888e8f98ff0debf4dcd560c38b8c07314400faca5db0a3013ed00f42049334`. These match the saved actor manifest for the scored implementation files and the final patch trace for SelfTests.
