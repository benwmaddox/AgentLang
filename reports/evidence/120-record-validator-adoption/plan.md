# Bounded record-construction adoption trial

Status: preparation only. Do not dispatch an actor until the full Debug gate finishes and the copied runtime pin is accepted. This is one bounded adoption observation, with no conventional-language comparison or general reliability claim.

## Task and question

Start the actor in a new, empty Flow/2 project and ask one concrete cross-field invariant task. The public task is:

Add a reusable BoundedRange record with start: Int and finish: Int, plus range.width(value: BoundedRange) -> Int, returning finish - start. A range is valid when start <= finish; equal endpoints are valid. Prevent reversed ranges from being constructed through the language, preserve width behavior for valid ranges, attach useful tests for accepted and rejected construction, run the tests, and commit at the highest maturity the current rules justify. Before closing, inspect the committed source, tests, and maturity.

The actor is not told validator syntax or required to query help. Record any help lookup only as descriptive process evidence. Keep values small so overflow behavior is outside this trial.

## Frozen independent oracle

Check these nine pairs after the actor finishes. For valid ranges, also assert range.width returns the listed result.

| start | finish | construction | width |
|---:|---:|---|---:|
| 2 | 5 | accepted | 3 |
| 0 | 0 | accepted | 0 |
| -3 | 4 | accepted | 7 |
| -8 | -5 | accepted | 3 |
| 7 | 7 | accepted | 0 |
| 5 | 2 | rejected with RECORD_VALIDATION_FAILED | — |
| 0 | -1 | rejected with RECORD_VALIDATION_FAILED | — |
| -4 | -8 | rejected with RECORD_VALIDATION_FAILED | — |
| 9 | 4 | rejected with RECORD_VALIDATION_FAILED | — |

## Acceptance

Require the exact public record fields and range.width signature; a pure BoundedRange -> Bool predicate that enforces the invariant at generated construction; passing actor-owned tests for at least one valid range and an expected-error rejected range; a committed project; and the highest maturity justified by the observed rules. If library publication is refused, preserve project maturity and record the structured gate evidence without weakening the invariant or tests.

The coordinator independently runs the nine-row oracle in a fresh read-only runtime against the committed project and inspects the exact persisted TypeSource.ValidatorTarget to confirm it identifies the predicate WordId. The actor's single broker session does not need to reload or launch a second runtime.

## Negative control

From a second empty project, run the same source and tests with an always-true predicate. Freeze the result separately: the reversed pair must be accepted by the mutant and the attached expected-error test must fail. Record any publication attempt and refusal if the control uses one. This only checks that the preflight can distinguish a missing invariant; it is not part of actor acceptance.

The two preflight controls exercise definition, attached tests, and direct evaluation only; they do not test project commit or manifest persistence. The coordinator's fresh read-only runtime check after the actor closes is the persistence check.

## Reused harness and preparation boundary

Reuse scripts/Start-SubagentTrialHostV2.ps1, report117's Flow/2 primer and JSONL trace format, the 100-exchange cap, and its restricted runtime operation allowlist. Do not write a new broker or generalized scorer. The copied Debug CLI is used for preflight without rebuilding; actor dispatch waits for the full gate to finish. Freeze the source revision, copied runtime file hashes, prompt, empty actor project, oracle, exact control requests, control outputs, and broker hash before dispatch.

Preparation is limited to this plan, the prompt, an empty actor project, one small direct-JSONL preflight runner/request set for correct and always-true controls, their logs, the copied runtime, and compact pin/freeze JSON. Manual coordinator review is enough for source, tests, and manifest identity. Target preparation time is at most 15 minutes once the current Debug artifact is available. If the copied CLI fails the focused controls, report the blocker; do not rebuild during the gate.

If dispatched, use one fresh gpt-6-luna actor at maximum reasoning with no inherited conversation, at most 100 broker exchanges, and no repository or verifier access. Preserve the raw trace and final response. Report only this bounded observation.
