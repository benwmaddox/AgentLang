# 125 — Matched pair repair: reuse works; no reliability lead

Six fresh external agents repaired the same pair-reminder defect. All six saved
solutions pass the twelve frozen independent scenarios, and all four Flow/2
functions retain library qualification. Both retained-vocabulary agents reused
the earlier agent-created helper; both F# agents reused its accepted equivalent.
The reset agents repaired the inline implementation. This demonstrates usable
reuse and correct edits in each condition, not a reliability advantage over F#.

One retained agent omitted the required `task.commit`. Its published revision
survives a fresh reload and passes acceptance, but it did not complete the
specified task-finalization workflow. Behavioral success and workflow completion
must not be collapsed into a single unqualified success claim.

## Design and controls

This follows the bounded comparison recommended in [report 124](124-efficacy-assessment.md).
Two independent `gpt-6-luna` agents at max reasoning ran per condition, without
conversation history or coordinator hints. At most three actors ran concurrently;
second replicas started as slots opened. The same public task described the
required repair without naming the affected function or optional helper. This
was location-unhinted repair, not blind discovery of an unspecified defect.

The retained Flow start contains the actual `invoice.queue-reminder-once` output
from [report 111](111-current-syntax-stateful-comparison.md). F# contains its
accepted equivalent, `queueOnce`. Reset contains the small typed foundation and
path helper without that learned operation. It is a compact reset condition,
**not reset-rich**, and does not test discovery in a large dictionary. Every arm
starts with equivalent duplicated pair logic and the same early-return defect.

The four newly supplied pair tests have matching inputs and return-only
assertions, including an incorrect expectation that encodes the defect.
Inherited helper tests are preserved prior actor outputs and differ in assertion
strength across languages. Nominal input types, path contracts, provider behavior
and capability preflight are aligned, but tools and inherited test topology are
not identical.

All three starts pass their own tests and fail exactly two independent cases:
first-ineligible/second-open and first-uppercase/second-open. Each scores 10/12.
Before dispatch, separately corrected Flow and F# controls passed 12/12, and the
oracle rejected wrong-second-path and content-preserving redundant-write controls
at 6/12 and 9/12 respectively. These are scripted control results, not agent results.

The final [oracle](evidence/125-matched-pair-repair/oracle.json) was independently
enumerated and reviewed before dispatch. It checks ordered results, full virtual
filesystem contents, aggregate reads/writes, duplicate identifiers, repeated
calls, custom/empty markers, case-sensitive statuses in both positions and
capability rejection before any provider calls. Predispatch review changed the
second ineligible status to `Open`; no oracle or scorer changes followed dispatch.
The [freeze](evidence/125-matched-pair-repair/freeze.json) pins all six starts,
prompts, 22 Flow runtime files, conventional broker and scorer. The runtime is the
unchanged report 121 build; repository HEAD was `414bd6f`.

## Agent outcomes

| Actor | Independent scenarios | Target own tests | Reused prior single-invoice helper | Required final step |
| --- | --- | --- | --- | --- |
| Retained 1 | 12/12 | 7/7 | Yes, two call sites | `task.commit` succeeded |
| Retained 2 | 12/12 | 6/6 | Yes, two call sites | **Omitted `task.commit`** |
| Reset 1 | 12/12 | 7/7 | Unavailable; inline repair | `task.commit` succeeded |
| Reset 2 | 12/12 | 6/6 | Unavailable; inline repair | `task.commit` succeeded |
| F# 1 | 12/12 | 6/6 | Yes, two call sites | Local validation succeeded |
| F# 2 | 12/12 | 5/5 | Yes, two call sites | Local validation succeeded |

Fresh copied-project scoring also passes each complete own suite. Flow totals
are 13, 12, 9 and 8 respectively; those include different inherited dictionaries
and should not be treated as comparative target-test denominators. All broker
sessions terminate through `host.close` with host/runtime exit zero. The retained-2
final account was unsure of exit after a later poll; the terminal trace establishes
it. No coordinator repaired the omitted task commit.

All four Flow targets are persistent library revision 2. Existing types, helper
revisions and inherited helper tests remain unchanged; the seeded target
expectation is corrected and new target tests are added. No new helper was created. In
both F# projects, `Domain.fs`, the project file, the inherited helper and inherited
test prefix remain unchanged. Scoring and mutation work uses copies; original
actor inputs are hash-verified unchanged. See [outcomes](evidence/125-matched-pair-repair/results/outcomes.json)
and [preservation evidence](evidence/125-matched-pair-repair/results/preservation.json).

Both retained agents produced this shape:

```agentlang
fn invoice.queue-reminders-for-pair(pair: InvoicePair) -> ReminderPairResult {
    effects fs.read, fs.write
    doc "Queue each invoice reminder in pair order and return its exact marker."

    let first-result = invoice::queue-reminder-once(pair.first);
    let second-result = invoice::queue-reminder-once(pair.second);
    reminderPairResult::new(first = first-result, second = second-result)
}
```

The retained target has 11/11 directly owned IR instructions and no branch
outcomes. The reset target has 49/49 instructions and 8/8 branch outcomes. This
is observable abstraction compression: the helper contains the branch logic.
It is not evidence of reduced transitive work, native memory usage or execution
speed. The inherited helper's own library tests remain part of the guarantee.
`finiteCoverage.complete` is true, but this Record-to-Record signature has no
supported enumerated input/return obligations; that flag is not additional
behavioral evidence here.

Source inspection finds explicit first-result then second-result bindings in all
six outputs. The oracle observes result order and aliased state, but does not
record provider-event order. Source ordering is separate static evidence, not a
claimed dynamic event trace.

## Regression detection

All six final own suites detect both planned regressions. In every Flow copy,
`replace-word` refuses publication with `COMMIT_TESTS_FAILED`. Both F# copies
build successfully but their unchanged tests fail. The frozen oracle also rejects
the F# mutants at 6/12 (wrong identifier) and 9/12 (redundant write).

| Actor | Wrong second identifier | Same-content redundant write |
| --- | --- | --- |
| Retained 1 | Detected; 5/7 target tests pass, publication blocked | Detected; 4/7 pass, publication blocked |
| Retained 2 | Detected; 4/6 pass, publication blocked | Detected; 4/6 pass, publication blocked |
| Reset 1 | Detected; existing-marker assertion fails, publication blocked | Detected; duplicate-ID and repeat count assertions fail, publication blocked |
| Reset 2 | Detected; existing-marker assertion fails, publication blocked | Detected; duplicate-ID count assertion fails, publication blocked |
| F# 1 | Detected by existing-marker assertion | Detected by duplicate-ID write-count assertion |
| F# 2 | Detected by existing-marker assertion | Detected by existing-marker write-count assertion |

The starts' return-only pair assertions do not distinguish the redundant write;
the agents add useful effect assertions. This is stronger behavioral evidence
than coverage alone, and it occurs in all three conditions. The language's
publication gate enforces the tests that exist; it does not independently invent
the right assertions. F# can express the same assertions in ordinary tests.
Evidence is retained in the six [post-mutation folders](evidence/125-matched-pair-repair/post-mutations).

The two predeclared mutations change only the second invoice's processing: use
the first identifier for its path; or read an existing second marker and write
back identical contents once. The latter preserves results and final state,
making effect assertions necessary to distinguish it. Actor tests are unchanged
in these post-solution probes. Gate-rejected Flow candidates are not scored as
if their unchanged committed revision were the mutant.

## Descriptive interaction measures

| Actor | Exchanges | Structured errors | Request bytes | Response bytes | Session seconds |
| --- | ---: | ---: | ---: | ---: | ---: |
| Retained 1 | 23 | 0 | 5,974 | 85,926 | 960.370117 |
| Retained 2 | 28 | 3 | 6,778 | 62,411 | 275.789969 |
| Reset 1 | 19 | 1 | 7,445 | 75,807 | 400.768035 |
| Reset 2 | 31 | 2 | 4,601 | 80,782 | 548.016636 |
| F# 1 | 24 | 3 | 11,042 | 25,132 | 488.913261 |
| F# 2 | 11 | 0 | 8,720 | 21,169 | 221.120201 |

These are exact broker payloads and full-session durations, including idle and
close. They exclude pre-broker setup failures. Exchanges are not equivalent
units of work across interfaces; payload bytes are not model tokens. Provider
token usage and controlled context-window limits are unavailable. Small samples,
concurrent execution and large within-condition timing variation preclude a
latency or efficiency superiority claim.

Trace errors include test-expectation syntax recovery, constructor-name discovery,
an empty-word description and missing diff fields. F# 1 had two malformed hash
requests and one failed validation caused by a test compile error. Retained 2's
self-report mentions two failed attempts; its trace contains three, which is the
number reported above. Successful protocol responses and passing test results
were checked separately.

## Interpretation and next decision

The intended vocabulary behavior occurred in both retained trials: agents found
and composed previously created, typed, tested operations. Conventional agents
also did so. The reset condition reached equal behavioral correctness without
creating a helper. Two agents per condition on one small task cannot establish
retention benefit, general reliability, scaling or smaller-context performance.

The task-finalization omission is concrete workflow feedback. A follow-up should
make an active task visible at close and distinguish function publication from
finished task logging; it should not silently claim successful task completion.
That is a bounded interface issue, not a reason to add more language mechanisms
before the next comparison.

This completes a compact version of the replicated comparison requested by report 124. Do not
keep repeating easy variants until a favorable difference appears. Continue the
already approved native-runtime work: move bounded mailbox dispatch/pending state
out of the .NET experiment host, then compare the two arena lifetime candidates
under matched memory and latency constraints. Future efficacy work should use a
held-out multi-function change with collateral-regression opportunities and an
independently fixed oracle. No result here proves LLVM performance or selects a
memory policy.

## Validation, evidence and limits

The [evidence index](evidence/125-matched-pair-repair/index.json) preserves prompts,
starts/finals, raw broker traces, final accounts, pins, scoring/control outputs,
mutated copies and preparation/review notes. Runtime/build binaries are excluded;
their hashes are pinned. Existing retention-004 drafts were not touched.

Local validation used the frozen task scorer for each of six final projects:

```powershell
& ./.agentlang/pair-repair-001/scoring/score-pair.ps1 `
  -Language flow `
  -ActorProject ./.agentlang/pair-repair-001/actors/retained-1 `
  -OutputPath ./.agentlang/pair-repair-001/results/retained-1-score.json
```

The other actor commands substitute their actor/output paths; conventional uses
`-Language fsharp`. Each runs own tests and twelve independent cases on fresh
copies. F# builds fresh; Flow verifies the exact pinned runtime and reloads saved
projects. Post-solution mutation and control commands/results are retained with
their copies. No product/runtime implementation changed, so a complete unrelated
solution acceptance rerun is not claimed. CI remains manual-only.

Preparation limitations remain explicit: the first F# corrected-control failure
output was overwritten before archival; its later passing result is not evidence
of first-attempt success. An initial Flow wrong-path candidate was refused by
tests, so its subsequent 12/12 score measured the previous correct revision. A
separate committed control changed one expected test value only to expose the
incorrect implementation to the independent oracle; that is not an own-test
escape claim. Those diagnostics are kept and labeled. A derived retained-2 mutation summary
read the wrong verification field; its incorrect original and the documented
correction are both retained. Raw mutation sources and probe results were unchanged.

Access restriction was by instructions and broker surface, not OS isolation.
Sandbox process setup failed on this workstation; actors used scoped host launches.
Some also used host execution to read only their supplied prompt; reset-2 candidly
noted this exceeded its prompt's launch-only exemption. No broader file access is
claimed from that, and the limitation is retained rather than erased. F# 1's
reported `Domain.fs` search matches were within its permitted project broker.
The final report relies on recorded mutations, preserved files and independently
scored outputs, not an assertion that every procedural instruction was followed.
