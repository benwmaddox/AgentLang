# 231 — Grouped renewal timing replacement comparison

Status: both participants complete; independently graded and source-reviewed.

Both agents implemented the grouped-time contract correctly. F# satisfied the published task after independent review; AgentLang failed the exact preservation requirement because it changed one unrelated inherited test input. This pair provides no reliability or interaction-cost advantage for AgentLang. It does demonstrate working nominal records, atomic replacement of real callers, retained identities/history and library qualification.

## Task and controls

Fresh `gpt-6-luna`/max agents received the same nominal `RenewalTiming` task through the existing broker. Replace monthly renewal's two timestamps with one record, migrate production callers, preserve existing behavior/cases/expectations, and add a direct regression. Inputs, nine common cases, controls and mandatory independent source review were frozen before dispatch. F# ran first, AgentLang second. [Report 230](230-grouped-renewal-timing-preflight.md) retains preparation/calibration evidence.

F# routes common and scheduled batches through one shared step; AgentLang has two separate step callers. This inherited architecture difference is not attributed to language design. AgentLang's 66 inherited creator observations and library qualification are separate from the nine common cases, with no equivalent F# case-count claim.

## Results

| Measure | F# | AgentLang |
|---|---:|---:|
| Independent common behavior | 9/9 | 9/9 |
| Separate inherited creator observations | Not matched | 66/66 |
| Participant validation | Fixed project passed | 160/160 attached tests |
| Raw automated overall score | Failure: static placement rule | Score-failure: inherited preservation |
| Independent task-level source review | Published task satisfied | Narrow preservation failure |
| Broker exchanges | 21 | 71 |
| Broker session seconds | 205.09 | 673.15 |
| Rejected broker responses | 2 | 9 |
| Request wire bytes | 8,167 | 21,826 |
| Response wire bytes | 39,851 | 253,071 |

Session duration excludes outside-session reasoning and coordinator grading. Wire bytes are protocol measures, not LLM tokens; token usage is unavailable. The exchange counts exclude transport closure. Both closure audits pass, with host/runtime exit codes 0/0 and no broker restart. F# applied six patches after two `HASH_INVALID` rejections and validated once. AgentLang used 22 test-source queries, 18 describe calls and 12 source calls; its other rejected requests concern a missing tests argument, a generated-constructor test attachment and function/type-name lookup.

The F# raw score remains **failure** because a lexical preservation rule rejects the new private regression helper outside `run()`. The published packet has no such placement requirement, and `run()` invokes the helper. Independent review verifies every non-generated file: only `Operations.fs` and `SelfTests.fs` changed, the protected four files are byte-identical, and inherited assertions/scenarios still execute at their original control-flow level. This is a static false positive resolved by the mandatory review declared before dispatch; it is not relabeled as an automated pass.

AgentLang persisted `RenewalTiming` first, then replaced the monthly function and both caller steps together with observed revisions 1/1/2. Stable IDs remain intact and revisions advance to 2/2/3. All three remain library-qualified; their commit ran 19 attached tests. All 284 referenced inherited source/test/example objects and old revision rows remain available. The independent suite and final `test-all` pass, the nominal-type gate passes, and scoring leaves the actor unchanged.

However, `subscription.renew-batch-step/errors` changes its `later-change.old-id` from `30000000-0000-0000-0000-000000000001` to `33000000-0000-0000-0000-000000000001`. That case short-circuits through an existing failed state, so its result is unchanged. It still violates the frozen requirement to preserve inherited cases: the edit is unrelated to monthly argument migration. No coordinator correction was made to the submission. The normalizer also flags two monthly assertion-strengthening edits; independent review finds those preserve fixtures and original expectations. The unrelated ID mutation remains a real scope failure.

## What this teaches us

The core can represent and publish this strongly typed contract change while retaining library qualification and historical objects. Passing coverage and behavior tests does not establish edit fidelity: an irrelevant test input can change without producing a failing assertion. Independent source comparison detected that here. Function history made the difference inspectable; it did not prevent it.

Discovery remains costly in this observation. The AgentLang participant tried `RenewalTiming.new` and the type name through function-oriented commands, while the generated constructor is lower-camel `renewalTiming.new`; search eventually exposed it. Constructor naming and generated type capabilities should be discoverable directly from a type description or definition response. This is a bounded interface improvement to investigate, not evidence that all agents have the same difficulty.

A second candidate is narrower replacement operations that preserve attached test metadata unless an agent explicitly changes it. This task required migrating some test calls, so a blanket ban on test edits would not solve it. Smaller semantic edit boundaries and a visible before/after test diff may reduce accidental fixture changes. Treat that as a hypothesis for the next implementation/comparison, not a proven benefit.

This pair favors F# on broker interactions and session time and does not support an AgentLang reliability advantage. Familiarity, interface payloads, caller topology, agent stochasticity and the fixed launch order are confounders. It cannot estimate success rates, causality, smaller-context performance or a general language advantage. The earlier report-228 pair also used fewer interactions in F#; more comparisons should follow concrete interface improvements rather than repeating unchanged preparation.

## Grading and security limits

Coordinator grading attempts are separate from participant errors. The first AgentLang grader request was rejected for an evidence path outside its permitted root before code ran. The next completed behavior scoring but omitted required control arguments. A corrected invocation then hit Windows path-length copying errors in an overly deep evidence directory. The final invocation uses the shorter prescribed root and frozen correct/swapped controls: controls complete, runtime pin verified, 9/9 common and 66/66 inherited, raw failure solely inherited preservation. These attempts and receipts remain evidence; no actor or frozen grader was changed to obtain a result.

Participant execution and independent grading remained in the sandbox. No new packages, network listeners, .NET escape or effect grants were introduced. Independent F# grading enabled NuGet audit but could not fetch advisory data (`NU1900`); no audit pass is claimed. Broker/no-web rules are not OS confinement. These pure business tasks do not validate untrusted native-code isolation, production security, arena throughput or mailbox performance. Existing trusted native-backend work remains a separate line of evidence.

Participant projects remain isolated trial endpoints, with their failures preserved. They are not promoted into the language implementation. The next small language improvement should target discoverable type/constructor metadata and precise replacement feedback, followed by another bounded agent observation.


## Evidence

The [verified comparison archive](evidence/231-grouped-renewal-timing-comparison/comparison.zip) and [member index](evidence/231-grouped-renewal-timing-comparison/index.json) retain both final actor states, traces, scores, failed coordinator attempts, frozen inputs and independent reviews. Archive SHA-256: `c3fde06689ddeb65784318c5145a3ca2e1ae0829445c960a458d805d16a2e07f`; 3,541 verified members. The archived report predates this footer. Read the [metrics](evidence/231-grouped-renewal-timing-comparison/metrics.json), [F# review](evidence/231-grouped-renewal-timing-comparison/reviews/fsharp.md) and [AgentLang review](evidence/231-grouped-renewal-timing-comparison/reviews/retained.md) separately; their source links refer to archived study paths. All 48 frozen pins and actor snapshots remained unchanged during grading and archiving.
