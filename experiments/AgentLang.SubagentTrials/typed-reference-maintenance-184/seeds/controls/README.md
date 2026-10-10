# Study184 calibration controls

`prepare_controls.py` derives four isolated Flow projects from the retained
seed and retypes the reference fields to `TrackingReference`:

- `correct`: exact per-shipment idempotence with inherited and focused tests.
- `always-append`: preserves the old behavior and has a matching replay test
  that expects the incorrect append.
- `global-dedup`: skips a reference found anywhere in the Store; its attached
  tests do not encode cross-shipment behavior.
- `global-dedup-wrong-selftests`: the same global-scope bug, plus a deliberately
  incorrect cross-shipment self-test that matches the bug.

Generated `.flow` inputs are written to `generated/`; isolated broker-project
copies, setup transcripts, response receipts, and source inventories are
written under `.agentlang/efficacy-maintenance-184/seed-draft/controls/`.
These are local oracle-calibration controls only. They do not represent
participant work and must not be dispatched.
