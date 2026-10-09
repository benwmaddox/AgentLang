# State-sensitive I/O maintenance comparison

Status: completed, 2026-10-09.

Both fresh participants produced implementations that pass all nine independent
acceptance cases. Existing behavior survives. AgentLang durably published a
library function with complete own-body coverage; neither participant composed
the existing publisher in its new function. This trial supports feasibility,
but establishes no reliability or vocabulary-reuse advantage over F#.

This probe extends the report-154 virtual-I/O repair into new state-sensitive
behavior. Two fresh external coding agents received matched feature contracts
with different workflow gates in AgentLang and F#: add non-overwriting configuration publication, preserve the
existing unconditional publisher and its caller, and supply regression tests.
The comparison uses one participant per environment. It can identify concrete
successes and failures, but cannot establish comparative reliability in general.

The new function distinguishes Created, Unchanged and Conflict. Independent
acceptance cases assert the outcome, exact source/destination/sentinel contents,
and read/write counts. Cases cover empty contents, Unicode, line endings and a
repeated call. Missing source and aliased paths are explicitly outside scope.

## Preflight evidence

The conventional fixture's Release build passed with zero warnings and errors,
and its four baseline self-tests passed. Its safe-publish target remains
unimplemented. The conventional broker also builds successfully in Release.

The AgentLang seed passed all four attached tests and committed both
`configuration.publish` and `configuration.refresh` at library maturity. Its
publisher and three tests originate from the accepted report-154 participant
output, reconstituted in the current dotted syntax. This is source provenance,
not an identical historical dictionary or revision. The refresh caller is new
coordinator-supplied seed vocabulary.

The conventional acceptance oracle passed nine of nine positive-control cases.
All three deliberately defective implementations were rejected: conflict
overwrite changed file contents and write counts; an unnecessary unchanged write
failed counts; an incorrect outcome failed the result assertion. The original
fixture source hashes were unchanged. Cases SHA-256:
`c31b6c3437ce5525aa00c9b45393e55b163cded7f470113253bd8decf2861c82`.

The language oracle also passes all nine positive cases and rejects the same
three defect categories with actual case failures, rather than request errors.
It seeds virtual files, observes a structured record, and checks the exact test
name and owner. Seven cases target the new function; one each protects publish
and refresh. All four control projects preserve the starting seed byte for byte.
These coordinator-written controls establish harness behavior, not agent efficacy.

Review of the first language-oracle draft found missing virtual-file setup and
incorrect handling of the existing publisher's Unit result as an enum result.
That draft failed definition validation. These are harness defects, not a
participant or product outcome. They were corrected and positive/negative
validation completed before dispatch. The private preflight attempt log records
the discarded attempts.

The dispatch manifest pins 64 prompt, runtime and starting-project files, plus
seven oracle/validation artifacts. Each fresh participant uses Luna with max
reasoning and no inherited conversation. Each receives its own project and
broker trace. AgentLang must add a library function, persist its tests and finish
the task session; F# edits the target implementation/tests and runs its fixed
validation project. Existing function definitions and baseline tests are frozen.

## Participant results

| Observation | AgentLang | Conventional F# |
| --- | --- | --- |
| Independent acceptance | 9/9 passed | 9/9 passed |
| New attached tests / test functions | 7 | 3 |
| All own tests under coordinator validation | 11/11 passed | 7/7 test functions passed |
| Participant validation | Passed | Blocked twice by NU1900 before compilation |
| Durable library publication / task finish | Both confirmed | Source edits saved; no language publication gate |
| Existing publisher called by new function | No | No |
| Broker exchanges | 37 | 19 |
| Error responses | 5 | 5 |
| Request / response wire bytes | 18,187 / 65,396 | 18,509 / 25,193 |
| Broker session duration | 334.2 seconds | 196.3 seconds |

The seven AgentLang tests invoke the target nine times. Its own coverage reports
22/22 executable instructions, 4/4 branch outcomes, and all three declared enum
return values. This evidence came from its own tests on a fresh verification
copy, separate from the independent oracle. Its commit and task.commit requests
succeeded; all three persistent functions retain library maturity and revision
1, with the two original functions' original test counts. F# baseline test bodies
and frozen provider/publisher/project files are unchanged.

The F# participant's validate requests failed because NuGet vulnerability data
could not be retrieved (NU1900 with warnings treated as errors). The coordinator
built a disposable copy in Release with `-m:1 -p:NuGetAudit=false`: zero warnings
and errors, all seven self-test functions passed, then all nine independent
scenarios passed. This confirms the implementation but does not turn those
requests into participant-completed validation. Future conventional fixtures
must pin an offline validation policy before dispatch; do not silently repeat
or relabel this participant run.

AgentLang's error responses comprise one history request without a word, one
rejected UTF-8 transport request, and three authoring diagnostics concerning
value-expression expectations and calling enum constructors. F# encountered one
rejected UTF-8 request, two malformed patch hashes, and the two environmental
validation failures. Both recovered from their transport error. These distinct
causes must not be combined into a claim about language type correctness. Both
brokers ended through host.close with host and runtime exit code zero.

## Implications

The library gate and deterministic effect providers can support a tested,
state-sensitive function without real I/O. Independent checks agree with the
accepted implementation in this case. Coverage is still not proof of the
requested behavior: assertions and independent acceptance remain essential.

The new function could have called the existing publisher in its creation
branch, with the same required effect counts. Both agents instead repeated the
read/write pair. That is a real absence of reuse in this small task, not evidence
that vocabulary growth is impossible. The helper hides only two primitive
operations, and the task did not impose reuse as an acceptance requirement.
Meaningful accumulated domain behavior remains the stronger future test.

There is no favorable cost signal here. AgentLang used more exchanges and
response bytes, including enum/test-syntax discovery and recovery. The F#
environmental validation failures and unequal tool workflows also prevent a
clean causal duration comparison. LLM token counts and provider turn counts
were unavailable. Preserve this result rather than adding easier trials to
manufacture a positive conclusion.

## Evaluation boundaries

Both fixtures use deterministic virtual I/O. This does not test a real filesystem
adapter. AgentLang observes explicitly named paths; the conventional scenario
runner can enumerate the provider map. Exact write counts help reject extraneous
writes, but named-path observations are not a general map-enumeration facility.

Participants must discover through their assigned broker. They do not receive
the oracle or controls. Prompt isolation is not an OS security boundary. Record
broker requests, errors, bytes and duration separately from correctness; protocol
bytes are not LLM token counts. Own tests, library qualification, independent
acceptance and preservation of existing behavior are separate evidence.

The [trial plan](../experiments/AgentLang.SubagentTrials/io-maintenance-156/PLAN.md)
defines the frozen contract and local validation procedure. No product runtime
code has changed for this probe.

Evidence includes pinned inputs and runtimes, both broker traces, participant
outputs, positive/mutant controls, final verification and independent scores.
The [archive manifest](evidence/156-state-sensitive-io-maintenance/manifest.json)
records 268 entries, 4,118,355 bytes, and archive SHA-256
`f31eb0e9cb1ec78edcc60e0b597a3cae2a7293ac8bce82ce4f65746fec4b3f2c`.
