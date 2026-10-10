# Existing abstraction and caller maintenance

Status: both fresh participants, independent behavior grading, preservation
review and verified evidence archive complete. All 37 full local Release checks
passed before committing this milestone. Local tests only.

## Outcome

Both fresh coding agents successfully added a trailing `dryRun: Bool` parameter
to their actual accepted subscription handoff implementation from report 163,
then updated two existing callers. Each saved submission passes 66 independent
handoff checks and 66 caller checks. Normal execution returns the updated store;
a successful dry run returns the original complete store after cancellation and
replacement validation. Error precedence remains correct in both modes.

The language submission reloads with all 149 attached tests passing and all three
changed functions at library maturity. Stable identities, historical revisions,
unrelated functions and types are preserved. Nevertheless, its test preservation
is incomplete: the missing-old test changed a replacement ID that previously
also exercised duplicate-error precedence, and its success test dropped explicit
pre-state active/uncancelled assertions while strengthening final record checks.
Those are limitations of the agent's change, not repairs made by the coordinator.
Behavioral correctness, library qualification and test preservation must be
reported separately. Coverage gates did not prevent this loss of prior evidence.

The conventional submission preserves all prior self-test assertions, adding
`false` to existing handoff calls and three dry-run tests. Inventoried foundation,
aliases, project files and dependency binaries remain unchanged. Its new tests
exercise successful unchanged-store results, creation validation and missing-old
precedence; they do not separately exercise already-cancelled or before-start
dry-run errors. The language agent adds a before-start dry-run case. Independent
grading covers these behaviors in both environments. All 12 mechanical
preservation checks pass; manual assertion review supplies the distinction above.

## Method

Two fresh `gpt-6-luna` agents with max reasoning ran sequentially, one per
environment, through bounded brokers with 100 exchanges available. They had
public task instructions and ordinary source/introspection access, without hidden
acceptance cases or coordinator coaching. AI agents remain external development
tools, not language runtime components.

Seeds preserve the accepted implementations from report 163. Annual and monthly
caller functions and their four tests were coordinator-created for this trial;
they are not claimed as prior agent-created abstractions. The language seed had
217 library functions and 146 passing tests. The conventional seed used its
frozen F# business foundation and retained self-tests. Both public tasks required
updating existing callers and preserving prior tests and unrelated source.

Inputs, prompts, oracle sources, actor seeds and runtime binaries were frozen
before dispatch. The pinned language runtime includes the uncommitted milestone
165 implementation. Its focused 1,507-assertion suite had passed; the full local
gate's older business-policy preflight was still running at dispatch. This timing
is explicitly recorded in the inventory. No source changed during either trial.
Concurrent validation may confound wall-clock comparisons. The full gate
subsequently passed before committing this milestone; its evidence is saved
with report 165.

The independent model has 33 fixtures per mode: 26 valid-reference cases and
seven orphan-reference cases. Annual/monthly callers each receive 33 fixtures.
Grading executes the saved language implementation without redefining it, and
compiles the saved F# Operations source in a disposable oracle project against
the frozen foundation. Participant self-tests do not supply oracle expectations.
Correct controls pass; fully executing faults that skip replacement validation
or return a changed store on dry run fail 16 and 10 dry-run cases respectively
in both environments. Language fault controls use project maturity and calibrate
behavior only; actual participant library qualification is assessed separately.

## Observations

| Measure | Language | Conventional F# |
|---|---:|---:|
| Independent handoff checks | 66/66 | 66/66 |
| Independent retained-caller checks | 66/66 | 66/66 |
| Broker exchanges | 37 | 39 |
| Protocol error responses | 3 | 4 |
| Broker session seconds | 505.774 | 622.817 |
| Request payload UTF-8 bytes | 26,660 | 9,442 |
| Response payload UTF-8 bytes | 81,023 | 192,054 |

Both sessions terminated through `host.close` with host/runtime exit code zero.
The F# participant's final narrative mentioned two rejected patches; the trace
records four: two invalid hashes and two ambiguous anchors. The trace is the
measurement source. All rejected operations left source unchanged.

The language participant tried unsupported Bool matching, recovered with `if`,
and tried a single-function signature change that correctly failed because the
old callers still supplied six arguments. It then discovered coordinated batch
staging and updated the target and callers together. Existing test source was
found through history after querying `source` on a test name failed; `tests`
currently lists test names rather than bodies. These are concrete discoverability
costs, not evidence that the agent required raw filesystem access.

An independent final language test run passed, but a coordinator summary helper
then assumed every successful response had a `data` field and failed on
"No active task." The summary was regenerated from the saved successful responses
after correcting that helper. Tests and participant source were not rerun or
repaired; the coordinator error is retained separately.

## Interpretation

The new batch staging supports a real signature-and-caller maintenance task,
preserves identities and validates the complete proposed dictionary before
activation. Both agents also reused the established cancellation/start rules.
This pair establishes feasibility, not a comparative reliability advantage.
One pair, differing tool interfaces and concurrent validation do not establish
causal latency or cost differences. Payload bytes are not LLM token usage;
broker exchanges are not model turns. No fixed token context budget was imposed.
There are no new native performance, arena or LLVM findings in this experiment.

The test-preservation finding argues for improving visibility of existing test
bodies before adding more language features. It does not justify treating 100%
branch coverage as a proof of correct behavior or silently rewriting trial tests.

## Saved evidence

The [archive description](evidence/166-handoff-signature-maintenance-agent-pair/README.md)
and [entry manifest](evidence/166-handoff-signature-maintenance-agent-pair/ARCHIVE-MANIFEST.json)
accompany the 6,134,542-byte ZIP. SHA-256:
`95e883a7c9fae7194c9227665961d983da328e462b682249590febe72d54133a`.
All 883 payload entries and ZIP CRCs were verified. It preserves frozen inputs,
pinned runtimes, seeds, final actors, broker traces, graders, controls, failed
preparation attempts, coordinator error and preservation reviews. Temporary
directories, generated bin/obj trees and disposable full project copies are
excluded. Referenced historical 161/163 oracle inputs are included; the historical
163 archive is not duplicated wholesale. Reports and roadmap remain ordinary
repository files rather than archive payloads.
