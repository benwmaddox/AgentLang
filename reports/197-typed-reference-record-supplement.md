# 197 — Typed-reference record supplement for reset-1

Status: the post-cohort representation-adapted supplement completed on the
sealed reset-1 snapshot. The original cohort result remains unchanged.

The frozen 190 Flow scorer used a scalar constructor expression for
`TrackingReference`. Reset-1 instead declares a nominal record with one
`value: String` field and uses `trackingReference.new(value = ...)`. The
original scorer therefore recorded 0/18 behavior cases executed and 18 setup
errors (`behavioral-or-type-rejection`). Those are representation/setup errors,
not observed replay-behavior failures. The original result is preserved at
`.agentlang/efficacy-maintenance-190/oracle-results/reset-1/result.json`.

The isolated 197 scorer adapts that representation boundary against the exact
captured reset-1 project. It keeps the original observer source, independent
model and 18-case file byte-identical. It requires the source declaration and
runtime metadata to describe exactly one `TrackingReference.value: String`
field with no validator; requires the `TrackingReference`, `Scan`, and
`ScanInput` constructor signatures and stack types; and checks both the
`ShipmentScanLookup.target` field and its accessor signature. Positive probes
and fixtures use `trackingReference.new(value = raw)` with no raw-String
fallback. The existing negative controls still require raw `String` and
`ShipmentId` values to be rejected where `TrackingReference` is expected.

Observation projection flattens only a record named `TrackingReference` with
exactly one String `value`. A different type name, field set, declared type or
malformed structured value is rejected; other records remain structured.
Behavior is not evaluated unless the representation, constructor, metadata and
helper gates pass.

The supplemental run passed all 18 unchanged full-Store/error expectations:
18 cases executed and 18 passed, including empty-reference and
whitespace/case-distinction vectors. Both type-negative controls passed, and
the scorer reported `participantSourceUnchanged: true`. The run used the same
pinned Release CLI as the original score. Its result lives separately at
`.agentlang/efficacy-maintenance-197/oracle-results/reset-1-record-197/result.json`.

This result is a separate representation-adapted score. It does not reclassify
the original 0/18 setup result, rank participants, or establish a general
reliability or vocabulary advantage. No participant repair or feedback turn
was made. The captured preservation, local-test, library, and task-finalization
evidence remain separate endpoints; this supplement does not claim to rerun
them. The nine focused Python tests cover adapter and projection controls,
including known-bad observations against the unchanged model. They are unit
controls, not end-to-end runtime calibration of a bad candidate.

The supplemental source, exact adapter diff, pre-run preparation and
parent-preflight receipts, unit-test output, complete filtered 197 result tree, original reset-1
result/capture/preservation audit, source and runtime pins, and original 196
evidence index are packaged in the [197 evidence archive](evidence/197-typed-reference-record-supplement/evidence.zip).
The [archive index](evidence/197-typed-reference-record-supplement/index.json)
lists the SHA-256 and byte length of each member. It contains 222 members,
376,790 bytes, SHA-256
`163d34077b2413dbe3df28a477257c91a36e7d51c55d59d52ef0acdddb58f566`.
Every member hash and length was verified after packaging. `bin`, `obj`,
`__pycache__`, `WRITE.lock`, and `.pyc` files were excluded.

Integrity checks confirmed the 87-file sealed snapshot inventory matches both
the original result and supplemental result, the capture candidate pins, and
the preservation audit, with zero mismatches. The copied `model.py` hashes
match at
`ce7f674ac361139476c1b21c7ec9a32158babac32b385efeb26c1a552cba61a8`; the
copied `cases.json` hashes match at
`1ff64efdff553279d835137cdff7ceaa6e27c3d4d74ff7f1621dc6f02beb5e22`; and the
original and supplemental observer source hashes match at
`cf083273a9dc5beb549c94aba7e9a9e823a118f176d8c15fe28b237e42fc8946`. The
pinned CLI hash is
`6e31d0cdd8c657c84e9f15762f83dc3b63ea619857b51cd2d8d6bd8cddaba3cc`. The
original 196 evidence archive was also checked against its index: all 1,360
members match their recorded hashes and lengths. The [196 evidence index](evidence/196-typed-reference-maintenance-cohort-results/index.json)
continues to identify the sealed original-cohort package; this supplement is
reported separately.

Validation commands:

```powershell
# From experiments/AgentLang.SubagentTrials/typed-reference-record-supplement-197/oracle
python -B -m unittest -v test_adapter

# From the repository root; rechecks current pins and every ZIP member
python -B .agentlang/efficacy-maintenance-197/package_evidence.py --verify

# Recorded supplemental scorer command; already completed with a fresh label
python -B experiments/AgentLang.SubagentTrials/typed-reference-record-supplement-197/oracle/score_reference.py --arm flow --project .agentlang/efficacy-maintenance-190/trials/snapshots/reset-1 --cli src/AgentLang.Cli/bin/Release/net9.0/AgentLang.Cli.dll --label reset-1-record-197
```
