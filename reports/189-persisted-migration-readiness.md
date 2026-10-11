# 189 — Persisted migration readiness

Status: the isolated persisted-project control passed. No new experimental
participant was dispatched in this stage.

[Atomic record evolution](188-atomic-record-evolution.md) now supports the
maintenance task that the [interrupted participant trial](185-typed-reference-maintenance-results.md)
could not complete through its frozen interface. This control copied the actual
unmigrated retained seed, including its hidden persisted store, rather than
constructing an already-migrated project. It used the accepted runtime from
`56dafc1`, without rebuilding or changing the language implementation.

The public V2 protocol supplied fresh source-hash tokens for three records and
revision tokens for six existing functions. One definition transaction introduced
the nominal String type `TrackingReference`, migrated the three record fields
and lookup helper signature, and added shipment-scoped replay idempotence.
Library publication and task finalization succeeded. Migration and reload
sessions closed normally, with audited host/runtime exits 0/0 after 46 and 43
exchanges respectively.

All 28 attached tests passed before publication and again in a fresh process.
The independent oracle executed and passed all 18 cases. Its positive nominal
construction control passed; both raw `String` and `ShipmentId` were rejected
where `TrackingReference` was required, with the expected structured type
diagnostics. The helper signature and static record metadata checks passed.

Preservation was checked separately from those passing tests. All ten existing
function identities and library maturity values survived. The existing public
entry signatures remained unchanged; the reference parameter of the lookup
helper was the intended signature change. All 21 inherited tests and three
examples survived, with only nominal-value wrapping allowed in the 16 adapted
sources. Seven tests were added. Exact prior type sources remained in history,
and the captured project loaded from persisted format 6 after migrating the
actual format-3 seed. The fresh reload left the captured project files unchanged.

The first runner attempt stopped in preflight before creating a trial directory.
PowerShell 7.6.5 parsed the fixed JSON clock as a date object, causing a string
comparison to fail. The runner now preserves JSON timestamps as strings. The
failure receipt records the observed command, error and diagnosis; no raw
transcript was saved for that attempt. The corrected `readiness-002` execution
passed without a runtime change. Source review also corrected an initialization
order error and made reused scorer checks depend on pinned upstream bytes,
rather than trusting a mutable adjacent receipt.

This control is scripted feasibility evidence, not an AI efficacy result. It
does not establish that agents discover the transaction, preserve assertions,
or benefit from accumulated vocabulary. The next step is a fresh six-participant
Luna/max comparison against the accepted runtime, separate from report 185.
Behavior, preservation, library qualification, task finalization and recovery
remain separate endpoints; protocol bytes are not model token measurements.

The control used an empty capability list and existing bounded local protocol
hosts. It added no network provider, arbitrary host invocation, dependency or
filesystem capability. This is not a security certification: the existing
real-filesystem provider's concurrent path-replacement race still requires a
protected project tree when that provider is enabled.

Evidence: [archive index](evidence/189-persisted-migration-readiness/index.json)
and [sources, trial captures and receipts](evidence/189-persisted-migration-readiness/evidence.zip).
The bundle preserves the runner and its review, failed preflight receipt,
accepted runtime pins, migration/reload traces, persisted candidate, inherited
source audit, and complete independent oracle results.
