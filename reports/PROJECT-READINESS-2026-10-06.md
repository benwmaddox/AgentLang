# Project readiness assessment — 2026-10-06

Scope: assessment of published milestone 054, based on the requirements ledger
and its cited evidence. The uncommitted list-fold document is a plan, not
delivered functionality. This assessment introduces no runtime changes and is
not a new controlled experiment.

## Current position

The language is a usable, substantially tested development prototype. It is
not yet a validated answer to the agent-development hypothesis. Much of the
language infrastructure exists; most comparative research evidence does not.
One percentage would obscure this distinction.

| Original milestone | Readiness |
| --- | --- |
| 1: core runtime | Implemented foundation: parser, checked values, dictionary, execution and REPL |
| 2: introspection | Substantial implementation; some commands and metadata remain incomplete |
| 3: types and effects | Strong nominal/refined and structured types implemented; effect checks exist, complete providers and resource-scoped capabilities remain |
| 4: tests | First-class tests and library instruction/branch coverage gates implemented; broader mock providers remain |
| 5: task sessions | Lifecycle, temporary words, persistence and rollback implemented; complete ordered research logging remains |
| 6: agent interface | Working small protocol and observed external-subagent workflow; clean fresh-context and comparative evidence remain |
| 7: benchmark application | Toy Customer/Subscription task exists; full matched business domain and executable suite remain |
| 8: controlled evaluation | Not completed |

Default Flow authoring now lowers through verified semantic IR. Strong types,
library quality gates, semantic editing, durable history, snapshots and
dependency inspection are substantial assets. Clean source CI for milestone
054 passed all 31 required validation checks. The number of checks and reports
does not establish agent usefulness or percentage completion.

The latest external agent composed a retained word into one new library word,
passed independent acceptance, and recovered from three language errors. It
inherited conversation history. A separate fresh-context trial was blocked by
approval review before definition. Neither measures efficiency against normal
F# editing. Negative controls demonstrate that full self-test coverage can
still admit incorrect business behavior, so independent acceptance is essential.

## Reviews we can make

An architecture and usability review is possible now. We can assess actual
syntax, inspection, types, errors, updates, tests, persistence and rollback.
That review can identify friction and simplify the design; it cannot conclude
that accumulating vocabulary reduces the cost of later correct changes.

A defensible preliminary purpose review needs a small controlled comparison:
several related changes performed by external subagents in Flat, Growing and
Conventional modes, with matched starting fixtures, independent acceptance,
the same model/settings, recorded interventions and repetitions. Growing must
accumulate vocabulary across tasks; one preseeded reuse task is insufficient.
Report success, runtime/tool interactions, recovery and available context/usage
measurements. If exact token counts are unavailable, state that limitation and
do not substitute bytes or runtime calls for tokens or model turns.

## Shortest useful next sequence

Updated after user prioritization: the existing small domain is sufficient for
the [five-task pilot](../docs/EARLY-AGENT-EVALUATION.md). Fold is a prerequisite
for the full business store, not for this first comparative sequence.

1. Freeze equivalent schema-only starting fixtures and independent acceptance
   for the small sequence. Finish bounded fold work already underway alongside
   trial preparation; do not make the pilot depend on the full business domain.
2. Complete the experiment adapter, observable metrics and fresh-context
   authorization path. Treat blocked trials as blocked rather than silently
   changing the experimental conditions.
3. Run roughly 5–10 matched sequential tasks across all three modes, repeat
   enough to expose variance, then publish an interim decision review.
4. Use that result to decide whether expanding to the full business fixture,
   executable 60-task suite and context-budget study is justified.

These are remaining work packages, not an elapsed-time promise. A matched trial
and an evidence review separate us from an interim
purpose assessment; the full study is substantially farther away.

## Judgment

Inspectable typed vocabulary and live semantic updates remain plausible value.
Coverage gates are useful quality controls, but do not prove behavior without
independent oracles. Low memory is presently an untested hypothesis: the
managed runtime has no implemented arena allocator, static-state mailbox or
LLVM backend. Those optional tracks should follow evidence of development
value rather than delay its measurement.

The main project risk now is building more infrastructure before measuring the
central hypothesis. The next decision milestone should prioritize comparative
agent evidence over additional optional language or memory features.

Sources: [requirements ledger](../docs/REQUIREMENTS.md),
[milestone 054](054-flow-renewal-agent-control.md),
[fold plan](../docs/LIST-FOLD-PLAN.md).
