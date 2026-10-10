# Typed reference maintenance seed drafts

`agentlang/common.flow` defines the shared Flow/2 model, public ingestion
signatures, append-every-valid-scan baseline, common tests, examples, and the
low-level reference traversal callback. The retained AgentLang seed adds
`agentlang/retained-reference.flow`, a qualified `shipment.find-scan` lookup
with its own documentation, tests, and example. The reset-rich seed uses the
shared callback directly and does not expose that wrapper. The F# seed has the
equivalent documented and tested `Shipment.findScan` helper.

The lookup helper is setup-created vocabulary. Any task history produced by
`prepare_seeds.ps1` records actual seed initialization; it must not be described
as work by an earlier participant. The three baselines use the same nominal
`ShipmentId`, record data, public entry signatures, errors, behavioral tests,
and examples. Tracking references are plain `String`/`string` at baseline.

These are draft inputs only. Freeze their hashes and derived projects only
after milestone 183 is accepted and the independent oracle controls pass.
