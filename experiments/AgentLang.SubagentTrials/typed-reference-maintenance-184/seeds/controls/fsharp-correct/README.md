# F# migrated correct control

This isolated calibration copy migrates the baseline's `Scan.Reference` and
`ScanInput.Reference` fields to the nominal `TrackingReference` type and keeps
the documented `Shipment.findScan` helper. The shared ingestion rule validates
status before checking the helper; a valid replay keeps the first scan and
returns the existing shipment unchanged.

The inherited eight baseline assertions remain intact. One assertion was added
to check that replay with a different valid status preserves the first scan
and the complete Store. No runtime, package, or project dependency was added.

The identical scoring project copy is under
`.agentlang/efficacy-maintenance-184/seed-draft/controls/fsharp-correct/project`.
Local build/test logs and source hashes are kept beside that project.
