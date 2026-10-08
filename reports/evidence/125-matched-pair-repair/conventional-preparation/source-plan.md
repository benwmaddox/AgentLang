# Conventional pair-repair fixture plan

## Scope

Create the conventional F# start project at `.agentlang/pair-repair-001/starts/conventional` and preparation notes under `.agentlang/pair-repair-001/conventional-preparation`. Keep edits within those paths. Reuse the existing conventional broker and validation profile; do not modify the broker, runtime, shared task contract, oracle, or product code.

## Seed and provenance

Start from the accepted conventional actor in `.agentlang/reminder-flow2-001/actors/conventional`. Preserve its nominal `InvoiceId` and `InvoiceStatus`, `Invoice` record, `Invoice.reminderPath`, `IReminderFiles`, `VirtualFiles`, existing `Execution.run`, `ReminderOperations.queueOnce`, and its attached seed and behavior tests. The actor's accepted `Operations.fs` and `SelfTests.fs` match report 111's archived source byte-for-byte:

- `Operations.fs`: SHA-256 `EAD0E00B3E60FD1FB385E0470E9B6A89A36F6FC6590E933ED858B9D62A0F67FF`
- `SelfTests.fs`: SHA-256 `25DB663221BAF124681B2B70F2C764A737581ABBAA1D87311F3B98BF96ABF2B8`

Do not replace the accepted helper with a placeholder or weaken its existing tests. Add the pair API on top of this foundation and retain the report 111 helper for discovery.

## Pair fixture

Add `InvoicePair` and `ReminderPairResult` records with lowercase `first` and `second` fields. Add `Execution.runPair allowRead allowWrite files pair operation`, where `operation` has type `IReminderFiles -> InvoicePair -> ReminderPairResult`. It must preflight both capabilities before invoking the pair operation and raise `InvalidOperationException("CAPABILITY_DENIED")` without provider calls when either capability is absent.

Add `ReminderOperations.queueRemindersForPair` with inline per-invoice processing. For each invoice, only ordinal, case-sensitive status `open` qualifies. On an open invoice, check its path once; read once and preserve exact existing contents when present, or write `queued` once when absent. Process first then second, allowing the second occurrence of the same ID to observe the first one's completed write. Keep the public intended behavior independent for each invoice.

Seed the same defect as the Flow fixture: when the first invoice is not open, return early with `{ first = "not-open"; second = "not-open" }` and make no provider calls, even if the second invoice is open. Add passing attached tests that preserve this old expectation alongside the pair filesystem cases. These tests should make the initial project green while clearly marking the first-ineligible expectation as the behavior to repair.

## Validation

Run a fresh Release build and self-test process from the start project:

```powershell
dotnet build .agentlang/pair-repair-001/starts/conventional/StatefulPilot.fsproj --configuration Release --nologo
dotnet run --project .agentlang/pair-repair-001/starts/conventional/StatefulPilot.fsproj --configuration Release --no-launch-profile
```

Record the final file hashes and command results. Keep the `.fsproj` and inherited helper code unchanged apart from adding required compilation entries only if the new code is split into another file.
