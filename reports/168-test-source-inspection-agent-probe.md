# Test-source inspection: fresh agent probe

Date: 2026-10-10. Status: completed; behavioral acceptance passes, prior test
assertion preservation remains incomplete.

One fresh `gpt-6-luna` participant with max reasoning naturally discovered the
new test-source view and inspected the target and both callers before editing.
Its saved implementation passes all 132 independent checks and all 150 attached
tests after reload. It nevertheless removed two explicit pre-state assertions
from the original success test. Inspection helped access; it did not ensure preservation.

## Setup

This is one language usability observation, not a matched comparison or causal
estimate. The report-166 prompt and 100-exchange limit are unchanged apart from
trial paths. The prompt does not mention `includeSource` or `caseName`.
No new conventional participant ran. The conventional audit is an unchanged
baseline self-comparison.

The participant used milestone `8726962` and the complete 22-file Release runtime
from report 167's terminal, passing 37-check local gate. No full validation ran
during this trial. Runtime/source pins and the 276-file initial project were
frozen before dispatch. The copied scorer changes only `EVIDENCE_ROOT`;
the independent model and expectations are unchanged.

The trial uses local synthetic projects and broker operations. No external
service testing or network capability was used. The participant reports shell
access only to read its assigned prompt and launch the broker. Runtime capabilities
were empty. This is not a general security audit or proof of isolation.

## Results

| Check | Result |
| --- | --- |
| Independent handoff behavior | 66/66 pass |
| Independent annual/monthly callers | 33/33 each pass |
| Attached tests after reload | 150/150 pass |
| Target and caller library maturity | All three retained |
| Existing names and stable identities | Retained |
| Task finalization | No active task |
| Mechanical preservation audit | 12 checks pass; includes conventional baseline self-comparison |
| Prior success-test pre-state assertions | Two omitted |

The scorer executes observers against a copy and does not redefine the target.
Target identity and persistent state remain unchanged during scoring. The
participant composes retained cancellation and creation functions, then returns
the original store for successful dry runs. Both callers forward `false`.

Calibration: the correct control passes 132/132. Skipping creation validation
fails 16 target cases; returning the changed store on dry run fails 10. Both
faults pass all 66 caller cases and are correctly rejected as target faults.
These inherited controls have project maturity; calibration does not establish
library qualification.

An initial control-copy attempt failed before CLI execution because its generated
Windows path was too long. Its partial copy and logs remain preserved as a setup
failure. The completed correct score was reused and verified; the two remaining
controls used shorter evidence labels without changing implementations or expectations.

## Discovery and preservation

The broker records 24 exchanges and a clean `host.close`, with host and runtime
exit codes zero. Test-source queries at exchanges 10, 13 and 14 precede the first
edit at 17. All request `includeSource: true`; none uses `caseName`. The target
query returns all five prior cases, including the vulnerable cases. Two failed
responses are `HELP_INVALID_ARGUMENT` and `FLOW_ARGUMENT_ARITY`; the participant
recovers through supported help fields and staging updated tests with the target
and callers.

Manual review matches cases by name, not the auditor's hash-sorted positions:

| Prior case | Preservation |
| --- | --- |
| `creation-rules-follow-cancellation` | Duplicate-ID input and error retained; adds `false` |
| `cancellation-rules-precede-creation` | Competing errors and cancellation precedence retained; adds `false` |
| `blank-term-after-cancellation` | Input and term error retained; adds `false` |
| `missing-old-precedes-creation` | Existing conflicting replacement ID and missing-old error retained; adds `false` |
| `success` | Post-state and unrelated-data assertions retained; explicit `before.status` active and `before.cancelled-at` none assertions removed |

Both callers' prior tests are byte-identical. Four dry-run tests are added,
including successful store preservation and creation-validation errors. The
mechanical audit checks object/identity boundaries, not individual assertions.
A separate read-only Luna/max review resolves cases by name in both manifests,
verifies object hashes, and confirms the same preservation findings. It reruns
no tests and is not a general runtime audit.

The broker session lasts 375.82 seconds and carries 13,094 request payload bytes
and 83,842 response payload bytes. These are protocol measurements, not LLM
token usage. No fixed token context limit was enforced.

## Interpretation

The source view is discoverable without special prompting. The competing-error
scenario weakened in report 166 is preserved here, but the same success-test
pre-state assertions are still omitted. One run cannot establish that the
interface caused either difference. Coverage and passing independent behavior
remain separate from preservation of prior evidence.

Close this bounded follow-up. Keep test-source inspection and independent
preservation review; do not add more similar trials or claim a comparative
reliability lead. Resume native semantic conformance and arena work separately.
Future substantial efficacy work should use a held-out change and explicit
collateral-regression criteria.

The [verified evidence manifest](evidence/168-test-source-inspection-agent-probe/archive.json)
and archive retain the trace, initial-input freeze, submitted projects, scoring,
controls, setup failure, reload checks, preservation diffs and review. Runtime
binaries and disposable scoring copies are excluded; runtime hashes and source
pins remain available.
The archive is 2,920,892 bytes, SHA-256
`e22db837fc2f698e36019ceed8b7125e02096302d9e34f005129205109d4461b`;
all entry hashes and ZIP CRC checks pass.
