# 149 — Validated billing windows and library reuse

Status: both fresh agents passed 18/18 hidden cases and reused the validated
API. This exploratory pair supports the API shape, not a language advantage.
Starting revision: `f7d1af6`; frozen preflight commit: `8692331`. No language/runtime changes.

Report 148 completed the batch task, but its agent replaced a call to a retained
Result-returning function after an early validity guard left a later error
branch uncovered. This probe asks whether a validated input type removes that
redundant error path without relaxing library gates.

## Prepared API and acceptance

Both languages now have a checked `BillingWindow` with signed 64-bit endpoints
and the invariant `from < until`. A checked constructor returns an option.
`rental.billable-in-window` / `Rental.billableInWindow` accepts that window and
returns an integer. The existing singleton API constructs the window and
delegates to the new calculation, preserving its Result/error contract.

| Check | AgentLang | F# |
| --- | ---: | ---: |
| Starter tests, including inherited tests | 41/41 | 48/48 |
| Inherited tests preserved | 29 | 37 |
| Separate composed-control tests | 47/47 | 49/49 |
| Unchanged external singleton oracle | 17/17 | 17/17 |
| Unchanged external batch oracle, control only | 18/18 | 18/18 |

Both controls validate once before traversal and reuse the total calculation.
The AgentLang control carries the window and running total in a record for its
statically named callback. The query meets 19/19 instruction and 4/4 branch
coverage; its callback meets 12/12 instructions with no branches. Both are
libraries, as are the new API functions and refactored singleton. There is no
Result accumulator, error-seeded traversal, or duplicate occupancy arithmetic.
The starter projects contain no batch query. Every external score reports its
input project unchanged. F# clean serial Release builds had zero warnings/errors;
its executable tests were run directly and through `dotnet run --no-build`.

## Fresh-agent results

One fresh Luna/max participant per language ran sequentially with report 147's
unchanged public batch contract and a 100-exchange limit. Both inspected the
available API, validated the window once before traversal, and reused the
validated-window calculation in their final production code. Neither duplicated
the occupancy arithmetic or used an error-seeded Result accumulator.

| Observed outcome | AgentLang | F# |
| --- | ---: | ---: |
| Hidden acceptance cases | 18/18 | 18/18 |
| Final own project tests/checks | 47 | 64 |
| Added tests/checks | 6 | 16 |
| Broker exchanges | 21 | 11 |
| Error responses | 2 | 0 |
| Reused validated-window calculation | Yes | Yes |
| Normal host/runtime exit | 0/0 | 0/0 |

AgentLang used dictionary discovery and source inspection. Its final query
calls `billing-window.try-create`; the fold callback calls
`rental.billable-in-window`. It introduced one accumulator record and two
library functions. The query has 19/19 covered instructions, 4/4 branch outcomes,
and both Result tags; its callback has 10/10 instructions and no branches.
Five tests attach to the query and one to the callback. The two rejected define
attempts were a stray semicolon and a nonliteral example expectation. Both were
repaired without coordinator hints or coverage changes.

F# used conventional file discovery and reads. Its added query matches
`BillingWindow.tryCreate`, uses `List.sumBy` with a closure calling
`billableInWindow`, and returns the existing domain error on invalid bounds.
Only `project/Rental.fs` and `tests/BillingWindowTests.fs` changed. Its compiler
and executable tests passed, followed by the same independent 18-case oracle.
The 47-versus-64 counts describe different test harnesses and baselines; they do
not establish relative thoroughness. Broker exchanges are not model turns or
token usage, and the AgentLang protocol includes explicit qualification/commit
steps that the conventional protocol does not.

Both oracles report unchanged participant inputs during grading. Final source
and dependency inspection confirms reuse; passing tests alone would not prove
that. The participants independently reported using only prompt verification
and their brokers. No participant communication was observed. Isolation remains
prompt-level: no per-participant tool whitelist was available. Model/reasoning
parity records the coordinator's requested Luna/max dispatch settings; broker
traces do not independently attest provider model metadata.

## Interpretation and next action

Report 148's agent completed the same task but removed a retained Result-returning
call after a redundant outer error path lacked coverage. Here the validated
input API removes that redundant error state before traversal, and the fresh
AgentLang participant both reused it and met unchanged library gates. This is
consistent with the hypothesis that strong API structure can reconcile reuse
with strict coverage. It does not prove why either agent made its choices.

F# also reused the API and reached the same correctness result with fewer broker
exchanges and no observed errors. This comparison establishes no AgentLang
reliability lead. Compared with report 148, AgentLang exchanges fell from 35 to
21, but these are different unreplicated agents and changed starters; this is
not a causal estimate of savings. No provider token accounting is available.

The API shape was selected by the coordinator. The result does not establish
that unaided agents naturally create good abstractions or that accumulated
vocabulary generally improves development. The next bounded test should ask
fresh agents to maintain these resulting compositions when the shared billing
rule changes, with hidden cases checking both singleton and batch consumers.
That directly tests reliable downstream edits and reuse without adding runtime
features or repeating preparatory infrastructure. Any resulting comparison must
retain both successes and failures and report source-level reuse separately.

## Preparation provenance and limitations

The first Flow preparator accidentally opened prior oracle-bearing control
files and stopped before implementation. The first F# preparator disclosed a
broad search exposing prior control/result matches. Their outputs are retained
as preparation evidence, not used as participant starts. Fresh preparators used
explicit read allowlists and independent copies of the original pre-batch seeds.

The clean Flow preparation preserved an incorrect expected control sum, corrected
before the independent oracle ran, and an initial attempt that lost uncommitted
candidates on CLI restart. No gates were weakened. One preparator created an
unused branch; the coordinator restored main and removed that branch without
changing source. Participant starts were inventoried copies of the clean
starters. All study source, commands, logs and failures live under
`.agentlang/validated-window-149/`; published preflight evidence accompanies this
report before participant dispatch.

## Frozen evidence

[Preflight archive](evidence/149-validated-window-reuse/preflight.zip) and
[entry index](evidence/149-validated-window-reuse/preflight-index.json): 2,381
entries, 6,436,480 bytes; SHA-256
`31613893bfb452713405ba25d1453a0e2c1a278b75fb07246aed099dcd56d242`.
Every archived entry was read back and hash-verified. The archive includes both
selected starts, separate controls, unchanged oracle copies, pinned runtimes,
prompts, commands, validation logs, preparation disclosures, and an independent
source review finding no blocker. Contaminated preliminary projects are included
only as historical evidence; participant start inventories identify the selected
clean projects explicitly.

[Results archive](evidence/149-validated-window-reuse/results.zip) and
[results index](evidence/149-validated-window-reuse/results-index.json): 278
entries, 466,366 bytes; SHA-256
`c00afb21ab3d5a54b272ecc33ea20ccba33d8168be71d7ad83489b6fe1eca63b`.
The archive preserves participant traces, final projects, independent scoring,
termination audits, post-test coverage/source inspection, and final review.
All 2,291 frozen nonparticipant inputs remain unchanged, final participant source
hashes match grading, and each results-archive entry was read back and verified.
The final independent review found no material discrepancy and identified the
model-metadata limitation recorded above.
