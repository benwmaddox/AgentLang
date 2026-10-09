# 143 — Subscription overlap comparison

**Status:** All four terminal submissions are independently scored and reviewed.
Preflight was frozen at 2026-10-09 05:53:31 UTC and published in `f281be4`.
One language submission fails an empty-interval rule despite passing its own
suite and library coverage. The other three pass all public cases. This study
establishes no comparative reliability advantage for the language.

## Participant behavior results

| Participant | Independent cases | Fresh submitted suite | Broker exchanges / error responses |
| --- | --- | --- | --- |
| Language A | 18/19 | 209/209 | 69 / 13 |
| Language B | 19/19 | 209/209 | 51 / 3 |
| F# A | 19/19 | 19 groups / 172 assertions | 42 / 11 |
| F# B | 19/19 | 19 groups / 165 assertions | 44 / 9 |

Language A rejects a request containing the timestamp of a subscription cancelled
at its start. That existing subscription has an empty occupied interval and must
not block the request. Its own passing tests do not expose this case. Independent
scoring stages all 38 expected oracle checks (two observations per shared case),
passes the 209 submitted tests, and fails only this one business case. All four
independent runs preserve their input projects.

The three other submissions pass every frozen public case. All brokers end with
explicit host.close and host/runtime exit codes 0/0. Both language tasks commit.
No submission was repaired by the coordinator before scoring.

The F# participants encountered NU1900 because the vulnerability audit service was
unreachable. Each temporarily disabled NuGet auditing through the broker to run
its suite, then restored both project files. Fresh coordinator builds on copies
explicitly use NuGetAudit=false; this avoids an environment failure and does not
certify package vulnerability status. The original unmodified broker validation
remains subject to that service failure. F# A additionally ran a final validation
after restoring configuration, which failed for that reason. These outcomes are
reported separately from source correctness and normal broker completion.

Error-response counts include authoring, discovery, patch and validation errors.
They exclude unsuccessful test results carried by successful protocol envelopes:
Language B has two such test batches (11 refinement failures, then three assertion
failures), later corrected. Broker requests are not model turns or tokens. The
interfaces and payload sizes differ, so these counts do not establish relative
reasoning cost. No controlled latency or token claim is made.

The bounded behavioral result is one of two language submissions passing all
cases, and two of two F# submissions passing. Four participants on one task do not
estimate general success rates. The observed language failure is nevertheless
real evidence against treating complete structural coverage as a guarantee of
correct domain behavior. The dimensions below retain that distinction.


## Qualification, scope and regression protection

| Dimension | Language A | Language B | F# A | F# B |
| --- | --- | --- | --- | --- |
| Public behavior | Fail: empty occupied interval | Pass | Pass | Pass |
| Fresh full submitted suite | Pass | Pass | Pass with audit disabled | Pass with audit disabled |
| Existing public signature and scoped edit | Pass | Pass | Pass | Pass |
| Current library qualification | Pass | Pass | Not applicable | Not applicable |
| Compiling boundary mutation rejected | Pass | Pass | Pass | Pass |
| Explicit task commit / broker close | Both pass | Both pass | Close passes | Close passes |

Both language submissions retain all inherited cases and minimally move the
successful-append test and example to the seeded expiry boundary. Each adds one
record and one fold helper, updates start, and adds 19 tests. Both F# submissions
change only Business.fs and Program.fs, preserving inherited test bodies and the
public start signature. Their temporary project-file edits are fully reverted.
No unrelated production behavior change was found.

Language A's start has 17 own tests, 93/93 instructions and 14/14 branch outcomes;
its helper has nine tests, 67/67 instructions and 10/10 branch outcomes. Language
B's start has 14 tests, 95/95 instructions and 14/14 branch outcomes; its helper
has 12 tests, 85/85 instructions and 6/6 branch outcomes. Fresh same-process
runs report library maturity, current coverage and complete applicable finite
coverage. Neither submission demotes a library. This structural coverage does
not cover every relationship among timestamps: A omits the guard that an
existing occupied interval must be nonempty. Its cancellation-at-start test
places the new interval after the empty point instead of containing that point.

Each reviewer changes a single strict overlap boundary to inclusive on a copy
or in a staged replacement, keeping submitted tests unchanged. All four mutants
compile and fail an adjacency regression. Language A's suite passes 205/209,
Language B's 208/209; F# A fails after 79 assertions and F# B after 76. A passing
mutation criterion therefore coexists with A's original independent behavioral
failure. It is evidence for this one regression, not general test completeness.

Language B's test named compares-instants-with-offsets duplicates canonical UTC
values, so the name overstates its own evidence. The independent case uses shared
raw offsets with the documented public normalization adapter. Language B also
reports an accidental second broker-launch attempt refused because the trace
already existed. The retained trace shows one session and normal finalization;
the attempted launch is a procedural deviation, not evidence of another editing
session. Do not treat all instruction adherence as clean merely because behavior
passes.

Reviewer setup errors are retained separately: an initial Flow mutation-source
extraction included unrelated attachments and failed authoring; the valid
compiled replacements are the scored mutants. An extra unsupported metadata
probe after Language B's successful fresh checks is also excluded from participant
error counts. No parse failure is counted as mutation protection.

## Interpretation and next decision

This is a normal reachable application change, unlike report 141's imported-state
maintenance task. The environment supports discovery, new typed vocabulary,
qualification and durable publication, but the observed behavioral outcome does
not favor it over F#. This is not a causal finding that F# prevents the error:
the two F# implementations explicitly include the missing empty-interval check.
Differences in interfaces, types, baseline tests and four participants limit any
broader comparison.

The actionable result concerns domain structure. Branch and finite-value coverage
cannot require a test for a timestamp relationship absent from the implementation.
A possible follow-up is an occupied-period abstraction representing either no
period or a validated nonempty interval. Test whether agents discover and reuse
that abstraction and avoid this mistake in *both* language and F# environments;
do not assume a new type automatically improves edits or change these submissions
to obtain a better result. Keep that bounded question ahead of additional actor
or mailbox infrastructure. The user's specialized-mailbox versus full-actor
choice remains open; this study supplies no evidence to settle it.


## Question and design

The study asks whether fresh agents can add a feature to an existing business domain, preserve unrelated behavior, and leave effective regression protection in two environments: the discoverable, library-gated AgentLang workflow and the existing F# reference project. This is a matched feature edit on reachable states. It does not use imported malformed state or add mailbox infrastructure.

The frozen task requires subscription.start to reject overlapping half-open periods for the same customer and product while preserving its public signature and validation precedence. Active subscriptions occupy [start, expiry); cancelled subscriptions occupy [start, min(expiry, cancellation)). A cancellation at start leaves an empty interval, and adjacent periods remain valid. Overlap returns SUBSCRIPTION_OVERLAP, and failure must preserve the input state.

The trial used two fresh Luna/max participants per arm, the same public task contract, and a 100-exchange cap. The freeze audit recorded four prepared starts and 774 checked project files: 382 in each AgentLang start and five in each F# start. The two starts within each arm have matching project inventories. At freeze time, participants/summary.json recorded dispatchCount 0 and freeze.json recorded no participant traces.

The freeze ledger is authoritative for the snapshot status and exact artifact hashes. The input filenames still use “draft,” and the rubric header still says it is not frozen; those labels were stale when freeze.json recorded frozen-before-dispatch. The archived copies preserve the original labels and files as hashed at freeze time.

## Coordinator control results

The final matrix used the same 19 independent cases in both arms. It verified unchanged-source hashes before and after runs. Both positive controls and both mutants were checked against the frozen scorer and their respective start projects.

| Control | AgentLang | F# |
| --- | --- | --- |
| Unchanged seed | Failed the same seven expected overlap cases; its 190 inherited tests passed. | Failed the same seven expected overlap cases. The pre-feature baseline smoke had passed 18 groups and 157 assertions. |
| Positive implementation | Passed all 19 cases and all 202 project tests. The 202 include 12 added tests. | Passed all 19 cases; the fresh Release build and test run passed 21 groups and 180 assertions, including 23 added assertions. |
| Inclusive-boundary mutant | Failed the three expected cases: adjacency at expiry, cancellation at the new start boundary, and cancellation beyond expiry. | Fresh build passed; the same three shared cases failed, including adjacency. The project test run also rejected the inclusive comparison. |

The unchanged Flow baseline smoke passed 190/190 inherited tests before the feature edit. The F# baseline smoke passed 18 groups / 157 assertions. These are baseline compatibility checks; the seven-case seed failures above are the independent feature oracle and are coordinator results, not participant outcomes.

The Flow inclusive candidate was defined and staged through Flow/2 before the unchanged tests ran. Its three failures are behavioral failures from the frozen shared oracle, not a parse or build rejection. The F# mutation changed the strict comparison to inclusive, built successfully, and was rejected by its adjacency test. Full logs and the exact mutation sources are in the archive.

## Setup corrections and comparability limits

The first Flow oracle setup wrote probe definitions into a project projection. The runtime continued loading its durable dictionary, so the probes were absent. I corrected the driver to stage definitions through the Flow/2 protocol and to run the tests in the same process. The frozen acceptance required successful staging and the expected cases; the inherited-only run did not count as acceptance.

The first unchanged-seed run also had a coordinator setup error: I passed raw -04:00 timestamps directly to Flow's UTC-only Instant refinement. That produced REFINEMENT_FAILED, not an overlap outcome, and initially made one of the seven Flow failures a false classification. I corrected the shared-case adapter to pass both raw values through the public instant.normalize API before calling subscription.start. F# accepts the same raw values as DateTimeOffset. The frozen raw case data remains identical, and the final runs now show the same seven genuine expected failures in each arm. If normalization fails, the Flow driver records that API error as the transition result.

The language positive control also needed two inherited fixture corrections. Its successful-append test and example started a same-customer/product period at the exact start of the seeded period, which conflicts with the new rule. The Flow control moved those two start instants from Jan 2 to the existing expiry on Feb 1, preserving adjacency, the successful-append purpose, IDs, assertions, and expected count. No other inherited Flow case was intentionally changed. The F# lifecycle test starts without an existing subscription, so its inherited tests needed no such adjustment. This asymmetry adds work to the Flow arm and remains a study limitation.

The domain structures also differ: AgentLang uses string-coded BusinessError values and exposes Store.subscriptions; F# uses a DomainError union with code/message mappings and stores subscriptions in a module-private map. Both implement the requested observable error, but their structural checks and inherited test baselines differ. Outcomes cannot be attributed solely to discoverability or library qualification.

Other coordinator setup issues were corrected before the freeze: the first conventional broker build used the wrong source path, and a PowerShell evidence-write binding error produced two files named 0 and 1 at the checkout root. The corrected conventional build completed with zero warnings/errors; the evidence files were moved intact into the control folder. These setup issues did not change repository source or the baseline projects.

## Pre-dispatch archive

The frozen evidence was archived before dispatch. The package includes the prepared starts and inventories, participant prompts and wrappers, controls, shared cases and scorers, final oracle results, raw logs, freeze record, and a snapshot of scripts/Start-SubagentTrialHostV2.ps1. It excludes generated bin/obj and temporary directories, lock files, and binary build outputs. A separate JSON index records each entry's uncompressed size and SHA-256.

[Preflight archive](evidence/143-subscription-overlap-comparison/preflight.zip) — SHA-256 1a9b30b1234ebc94c429b19a3f584238cbce6ef89dcecd4c25081ae0f8a668a0, 27,293,229 bytes, 5,799 entries. [Entry index](evidence/143-subscription-overlap-comparison/preflight-index.json). The packager verified every archive entry against both the index and current source bytes after writing the archive.


## Terminal evidence and local validation

The [results archive](evidence/143-subscription-overlap-comparison/results.zip)
contains terminal participant projects and raw broker traces, dispatch metadata,
independent oracle outputs, fresh suite/qualification checks, exact source and
mutation diffs, all reviewer attempts, and before/after integrity records. Its
[entry index](evidence/143-subscription-overlap-comparison/results-index.json)
records 3,937 entries, independently checked against source bytes and ZIP contents.
The archive is 18,911,029 bytes, SHA-256
`03dd1e230d274a5fe4b777162809475f954265c0584a8cf46f87f62ed7e9abdb`.

The final audit verifies all 39 frozen protocol/runtime files, four terminal
traces, all four normal-close audits and 838 participant source/store files
against their pre-grading inventories. The coordinator made no participant repair.
The original pre-dispatch archive remains unchanged. Build outputs and lock files
are excluded from both source-invariance comparisons and the archive.

Validation used the frozen `oracle/score.py` for all four submissions, the existing
`Audit-SubagentTrialTerminationV2.ps1` for every trace, fresh same-process language
suite/description requests, and fresh serial F# Release builds followed by
`dotnet run --configuration Release --no-build`. F# builds explicitly used
`-m:1 -p:NuGetAudit=false`; exact commands, stdout/stderr and exit codes are retained.
Every boundary mutation compiled before its unchanged-suite behavioral failure.
The repository runtime/compiler source did not change in this study, so the broad
runtime implementation gate was not rerun. Documentation diff checks pass; CI
remains manual-only.


## Limits

These controls establish that the frozen oracle distinguishes the unchanged seeds, the positive examples, and an inclusive-boundary defect. Those coordinator controls alone do not measure participant performance. The four-participant study is too small to support a broad statistical superiority claim. No token-use claim is made because no authoritative usage data is available.
