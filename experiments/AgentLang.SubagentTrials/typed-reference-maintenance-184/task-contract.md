# Draft task contract — not frozen or dispatched

Purpose: held-out maintenance of a pure data model, using current interpreters
and the existing V2 brokers. Identical semantic types/public contract across
retained, reset-rich and F# arms. Vocabulary availability is the treatment;
this single stage cannot establish accumulation across agents.
Retained AgentLang and F# both provide an equivalent documented/tested reusable
reference-lookup helper. Reset-rich uses lower-level traversal with the same
entry contract. This is setup-created vocabulary; no prior participant history
is fabricated or claimed.

## Baseline model

- `ShipmentId`: nominal String without predicate, common to every arm.
- `Scan`: `reference: String`, `status: String`.
- `Shipment`: `id: ShipmentId`, `label: String`, `scans: List<Scan>`.
- `Store`: `shipments: List<Shipment>`, `audit: String`, `generation: Int`.
- `ScanInput`: `shipmentId: ShipmentId`, `reference: String`, `status: String`.
- `ScanError`: closed alternatives `UnknownShipment` and `InvalidStatus`.

Semantic entry operations (exact source spelling may follow current parser):
`shipment.ingest(Store, ScanInput) -> Result<Store, ScanError>` and
`shipment.ingest-batch(Store, List<ScanInput>) -> Result<Store, ScanError>`.
The baseline appends every valid scan. Accepted statuses are exactly
`received`, `in-transit`, `delivered`. Unknown shipment takes precedence over
invalid status. Preserve shipment order, labels, unrelated scans, audit and
generation. Batch processes in input order, stops at the first error and returns
that error; the original immutable input is unchanged. Successful batch is the
ordered fold of single-event behavior; empty batch returns the input Store.
Use no IO, providers, mutable globals or new host primitives.

## Requested maintenance

Create nominal `TrackingReference` over String without predicate. Make Scan and
ScanInput reference fields use it. It must remain distinct from String and
ShipmentId even if raw text matches. Update the shared ingestion rule and both
callers. A valid replay on the same shipment preserves the complete Store and
keeps the first scan/status; the same reference on another shipment is new.
Check the existing unknown-shipment/invalid-status rules before deduplication.
Equality is exact raw String identity; no trimming or normalization.
Participant chooses new helper names and implementation. Preserve existing
seeded helper names and identities while adapting reference types. Existing behavioral
assertions/examples must survive; constructor inputs may be retyped to the new
schema without changing expected domain values or removing assertions.

## Independent acceptance (18 cases)

1. First scan into empty scan list.
2. New reference appended after an existing scan.
3. Same-shipment replay with same status.
4. Same-shipment replay with a different valid status keeps the first scan.
5. Same reference on another shipment is independent.
6. A reference whose raw text equals the ShipmentId is still a distinct type.
7. Empty batch is identity.
8. Replay within a batch appends only once.
9. Batch replay of pre-existing reference is identity.
10. Multiple fresh references preserve input order.
11. Interleaved shipments preserve original shipment order and unrelated scans.
12. Unknown shipment produces UnknownShipment.
13. Invalid status for known shipment produces InvalidStatus.
14. Unknown shipment and invalid status produce UnknownShipment.
15. Replay with invalid status still produces InvalidStatus.
16. Batch stops at first invalid status (not a later unknown shipment).
17. Empty reference is valid and deduplicates exactly.
18. Whitespace/case differences are distinct references; no normalization.

Every successful case compares full state, including sentinel audit/generation
and unrelated shipment labels/scans. Every error compares error identity and
checks that original input remains unchanged. Type-negative controls separately
attempt a bare String and ShipmentId in the TrackingReference field. Inherited
evidence preservation, authored library qualification, finalization and recovery
are separate endpoints, not components of a blended success score.

Calibration: correct control passes; always-append fails replay cases;
global-dedup fails cross-shipment case; global-dedup with matching incorrect
authored tests may qualify locally but must fail the independent oracle. Use
complete local test results and disclose any calibration setup failure.

Draft preparation may happen while183 gates run. Freeze committed source,
seeds, prompts, vectors and oracles only after183 is accepted and published;
no participant dispatch before calibration. Keep unrelated retention004 files.
