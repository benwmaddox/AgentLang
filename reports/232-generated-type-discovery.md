# 232 — Generated operation discovery

Status: implementation, paired discovery probe and full local validation complete.

Type descriptions now advertise the actual generated constructors and accessors. A fresh Luna/max agent completed a small record-discovery task in three broker exchanges with the updated runtime, compared with six on the previous runtime. Both succeeded without errors. This is a narrow usability observation, not evidence of a general reliability or development-cost advantage.

## Change and motivation

[Report 231](231-grouped-renewal-timing-comparison.md) recorded an agent guessing `RenewalTiming.new` when the generated record constructor was `renewalTiming.new`. The existing `describe` request with a `type` name now includes `generatedOperations`: actual dictionary names, input/output signatures, effects, semantic operation kind, exact Flow/2 references and reference-unavailability reasons. Record constructors also expose ordered named parameters; accessors expose their field; enum constructors expose their case.

The metadata comes from ownership tags on the effective dictionary snapshot, rather than a second naming convention. Ordering follows the schema. Authored definition responses retain their existing shape. Parser, compiler, typed IR, ABI, effects and execution semantics are unchanged. This is an additive JSON response change.

## Agent probe

Two fresh `gpt-6-luna`/max subagents received the same record-only seed and task: discover `RenewalTiming` and construct a record with `at = 10` and `expires-at = 20`, without editing definitions. The updated runtime ran first, then the previous accepted runtime. Inputs, source hashes and private runtime hashes were frozen before dispatch. Both used the existing V2 broker with only help, describe, words, search and pure eval, no effect capabilities and a twelve-exchange limit.

| Observation | Updated runtime | Previous runtime |
|---|---:|---:|
| Exact typed record result | Pass | Pass |
| Broker exchanges, excluding close | 3 | 6 |
| Rejected runtime responses | 0 | 0 |
| Request wire bytes | 144 | 263 |
| Response wire bytes | 4,633 | 13,381 |
| Broker session seconds | 44.85 | 41.19 |

Updated sequence: help, describe type, eval. Previous sequence: help, describe type, search, describe constructor, help, eval. Both returned `RenewalTiming { at = 10, expires-at = 20 }` with type `RenewalTiming` and no effects. Both closed the same broker process cleanly, with host/runtime exit codes 0/0. The previous participant's self-report counted seven exchanges including close; the authoritative trace contains six broker exchanges plus close.

Coordinator preflight verified the same correct output on both runtimes and distinguished a swapped-field result. Post-trial grading verified 39 source, runtime, frozen-input, seed and actor-file hash checks. Protocol bytes are not LLM token counts. One tiny task, fixed order and two agents cannot establish causality or an efficacy trend; elapsed time did not improve here.

## Validation

Focused runtime validation passed 45 groups and 2,351 assertions. Tests check record/scalar/enum metadata against individual word descriptions, execute advertised references, verify reversed named constructor arguments, retain deterministic metadata after persistence/reload, and preserve authored-only definition responses.

Full serial local validation passed all 37 checks, with package auditing enabled and no skipped audit. Its business-policy preflight passed 98 checks across 30 independent expected outcomes. The validation command was `scripts/Validate.ps1 -SerialBuild -ReportPath .agentlang/type-discovery-232/validation/full-host.json`, using workspace-local TEMP/TMP and the SDK workload resolver override.

Initial sandbox validation stopped at NU1900 because it could not reach the package-vulnerability feed. The trusted repository validation was retried on the host; participant execution remained in the sandbox. Earlier focused attempts also retained SDK workload-resolution and system-temp-access failures, followed by a passing workspace-temp execution. Passing package auditing is not a comprehensive security review.

## Boundaries and next step

No new primitives, dependencies, external effect providers, network listeners or arbitrary .NET entry points were added. The broker operation limits and language effect policy are useful controls; they do not establish OS-level confinement or guarantee the absence of security defects. This milestone improves discoverability without widening runtime authority.

The larger reliability question remains open. Report 231 still records an unrelated inherited test edit. The next bounded candidate is improving preservation of test metadata during definition replacement, followed by another independently graded edit task; this discovery probe does not substitute for that comparison.

## Evidence

[Evidence archive](evidence/232-generated-type-discovery/evidence.zip), [member manifest](evidence/232-generated-type-discovery/manifest.json) and [verification receipt](evidence/232-generated-type-discovery/receipt.json) retain 88 members, including source snapshots/diff, focused/full validation receipts and logs, frozen packets/launchers, controls, traces, grading and the tentative follow-up plan. Archive SHA-256: `d7fa39bc63e3cb862e759b8f2880f6aeda3d25f51a44999f52d37db159785ad7`.

Private runtime binaries, scratch/temp directories and generated build/cache outputs are excluded; runtime hashes are retained. Frozen final launchers are authoritative: they include workspace-local scratch TEMP/TMP settings added after preparation; the retained preparation template alone does not reproduce those launcher bytes. Failed preparation attempts are retained separately and are coordinator failures, not participant outcomes.
