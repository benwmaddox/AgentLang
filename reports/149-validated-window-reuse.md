# 149 — Validated billing windows and library reuse

Status: API feasibility and independent acceptance checks pass; fresh-agent
outcomes pending. Starting revision: `f7d1af6`. No language/runtime changes.

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

This establishes feasibility using existing strong types. It does not yet show
that an unfamiliar agent discovers the API or benefits from it.

## Fresh-agent comparison

Prepare one fresh Luna/max participant per language, run sequentially using
report 147's unchanged public batch contract and a 100-exchange broker limit.
Use report 148's corrected AgentLang guidance and report 146's conventional
file/schema discovery. The prompt requires inspection and tests but names no
preferred helper or algorithm. Record correctness, actual production reuse,
coverage qualification, errors, edits, and normal broker termination separately.

Explicit instructions prohibit sibling inspection, messaging and delegation.
This is prompt-level isolation: no per-participant tool whitelist is available.
The API shape was selected by the coordinator. This experiment cannot establish
natural abstraction invention, causality, or a population-level language advantage.
Neither fresh participant has been dispatched at this reporting point.

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
changing source. Final participant projects are inventoried copies of the clean
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
