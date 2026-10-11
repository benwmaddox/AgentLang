# 216 — Typed batch failure maintenance preflight

Status: reviewed preparation passes on 2026-10-11; no fresh participant result.
The next study tests whether external coding agents can maintain a typed library
API while preserving existing behavior and evidence. It is a continuation of
the two accepted 213 projects, which contain different participant-authored
implementations. It is not a clean causal retention comparison or an F# comparison.

The public change is
`subscription.renew-batch(Store, List<SubscriptionRenewal>, Instant)` returning
`Result<Store, SubscriptionRenewalFailure>`, with `index: Int`,
`old-id: SubscriptionId`, and `cause: BusinessError`. Failure must identify the
first failing input, using a zero-based position and the exact existing error
code and message. Success preserves every Store field and list order; empty
input succeeds unchanged. The caller's input remains unchanged.

Correct public-interface controls publish the migration in both projects,
preserving root/helper IDs and library qualification. Fresh-process reload passes
150/150 reset tests and 153/153 retained tests, plus all 39 example owners.
Independent observation passes 9/9 cases in both, and all 66 retained creator
regressions. An independent source review checks all 11 reset and six retained
inherited root/helper tests: their scenarios, fixtures, success expectations and
underlying causes survive the necessary type/wrapper/counter adaptations. Each
arm adds two tests for later-item localization and first-error precedence.

The adapter checks complete typed results, nominal record fields, IDs, library
qualification, inherited objects/tests/examples, process completion and unchanged
inputs. Eight fixture groups pass, including malformed values, wrong nominal
types, missing responses and preservation failures. Added tests/examples are
allowed while inherited evidence and duplicate multiplicity remain protected.
The adapter keeps overall acceptance false until independent source review;
coverage cannot establish that an edited assertion still tests the intended rule.

Runtime fault calibration is recorded separately from local library tests. The
always-zero-index mutation should satisfy only six of nine true specifications;
the last-error-wins mutation should satisfy eight. Every observed result must
also match its declared fault prediction. Deliberately failing assertions are
diagnostic evidence; these faulty definitions are never published to the library.
All four controls execute nine cases with zero CLI exit, exactly 11 responses,
no observation setup errors and unchanged projects. The observed counts are:

| Control | Reset matches specification | Retained matches specification | Matches declared fault model |
| --- | --- | --- | --- |
| Always-zero index | 6/9 | 6/9 | 9/9 in each arm |
| Last-error wins | 8/9 | 8/9 | 9/9 in each arm |

Their local test suites fail as expected; no faulty candidate is committed.
Readiness independently verifies original actors, initial participant copies,
baselines and all source/runtime pins before sealing.

Preparation failures remain evidence. Targeted commit, replace-word and a group
commit in a different process failed before the successful publication control.
Record evolution stages an atomic candidate group, so define, test and untargeted
group commit must share a live CLI session. The successful control then reloads
in a fresh process. The first four fault controls also failed before observation:
their replacement request omitted the existing failure record's source hash.
The compiler rejected the atomic definition. These are observer setup errors,
not agent outcomes or runtime defects. Review also corrected a calibration gate
that confused matching the fault model with matching the real specification.

An earlier read-only describe/test-all probe used original 213 actor paths rather
than disposable copies. No definition or commit targeted them, and full project
fingerprints remain unchanged. Subsequent authoring/scoring uses copies. This
deviation is disclosed rather than treated as compliance with the copy-only rule.

The trial uses the accepted interpreter pinned to 37 source files and five runtime
files, virtual filesystem providers and empty effect grants. It adds no network
listener, external service, arbitrary .NET access or new primitive capability.
Broker restrictions remain prompt-governed rather than an OS security boundary.
This is bounded preparation, not a comprehensive security audit.

The [plan](../experiments/AgentLang.SubagentTrials/typed-batch-failure-216/PLAN.md)
predeclares fresh Luna/max participants, retained then reset in the recorded
random order, sequential runs and a 100-exchange limit. Controls and outcome
feedback are withheld. Fresh trials begin only after readiness verification and
input freezing. Reliable-edit superiority, causal savings and native performance
remain unproven; the [215 efficacy decision](215-efficacy-after-paired-vocabulary-pilot.md)
still governs interpretation.

Evidence: [independent reviews](evidence/216-typed-batch-failure-preflight/review.md)
and [sealed preparation index](evidence/216-typed-batch-failure-preflight/preflight.index.json).
The archive retains complete controls, project copies and failed setup receipts;
compiled binaries and Python caches are omitted. Reports distinguish source
review, model fixtures, runtime calibration and participant outcomes.
