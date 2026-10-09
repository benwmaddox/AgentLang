# 143 — Subscription overlap comparison

**Status:** Preflight frozen at 2026-10-09 05:53:31 UTC. The archive was captured before dispatch. At the time this report was updated, all four participants had been dispatched; no participant results were available. This report documents the frozen setup and coordinator-run controls only.

## Question and design

The study asks whether fresh agents can add a feature to an existing business domain, preserve unrelated behavior, and leave effective regression protection in two environments: the discoverable, library-gated AgentLang workflow and the existing F# reference project. This is a matched feature edit on reachable states. It does not use imported malformed state or add mailbox infrastructure.

The frozen task requires subscription.start to reject overlapping half-open periods for the same customer and product while preserving its public signature and validation precedence. Active subscriptions occupy [start, expiry); cancelled subscriptions occupy [start, min(expiry, cancellation)). A cancellation at start leaves an empty interval, and adjacent periods remain valid. Overlap returns SUBSCRIPTION_OVERLAP, and failure must preserve the input state.

The planned trial has two fresh Luna/max participants per arm, the same public task contract, and a 100-exchange cap. The freeze audit recorded four prepared starts and 774 checked project files: 382 in each AgentLang start and five in each F# start. The two starts within each arm have matching project inventories. At freeze time, participants/summary.json recorded dispatchCount 0 and freeze.json recorded no participant traces.

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

## Limits

These controls establish that the frozen oracle distinguishes the unchanged seeds, the positive examples, and an inclusive-boundary defect. They do not measure participant performance. The planned four-participant study is too small to support a broad statistical superiority claim. No token-use claim is made because no authoritative usage data is available.
