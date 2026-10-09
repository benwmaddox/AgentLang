# Batch billing: reuse of an earlier agent's domain function

Status: all four participants finished and were graded. Both F# submissions pass
18/18 independent cases. Both AgentLang participants abandoned the task and
rolled back after failing to discover the supported Flow/2 fold syntax.

Study limitation discovered after dispatch: both AgentLang participants reported
exchanging information about their fold-syntax difficulty. Those runs are not
independent observations. The coordinator prohibited further communication,
maintained the original Flow/2 constraint, and requested the exact exchange.
Interpretation must distinguish this incident from the directly reproducible
help/grammar mismatch; no four-cell independent efficacy claim is supported.

Report 145's four agents produced correct single-rental billing queries, but
neither retained-start agent used the supplied interval abstraction. This next
comparison retains the actual queries authored by that report's flat-start
agents. It asks whether another agent discovers and composes that accumulated
domain behavior for a batch query, without requiring reuse.

## Comparison

Four fresh Luna/max participants receive the same public task, one per language
and flat/retained condition, with 100 broker exchanges each. AgentLang uses the
frozen report142 CLI; F# uses report146's bounded project/operation discovery.
The F# discovery change means cross-report interaction counts are not a clean
measure of vocabulary benefit. This is one observation per cell, not a population
reliability estimate.

Retained starts include report145 flow-b's `rental.billable-days` and fsharp-a's
`Rental.billableDays`, their documentation and tests. Flat starts are their
original starting projects. No interval abstraction is added. Common F# README
text is neutral and identical across conditions. AgentLang additions must qualify
as libraries; F# uses its ordinary compiler and executable tests. Inherited APIs
and tests must survive. These are different development environments, not equal
proof systems.

The task totals each rental's occupied integer days within a requested half-open
window. Duplicate and overlapping rentals count independently; invalid windows
must fail even for empty input. Signed 64-bit request endpoints require careful
arithmetic. The independent oracle enumerates bounded stored days, checks public
fixture construction, and checks preservation of every input entry and its order.
Its 18 cases include cancellation, empty lists, extreme bounds and permutations.

Correct controls must pass before dispatch. A compiling maximum-instead-of-sum
control must fail on multiple contributions. Missing-query starters verify absence,
not behavior. Oracle inputs, prompts, starting files and runtime artifacts are
frozen before agents launch. Production calls establish reuse; reading or retaining
a helper alone does not.

## Feasibility finding

The existing language can express a static list fold that calls the retained
query. Its accumulator carries the requested bounds and running total. The
coordinator's first implementation rejected invalid bounds early, making a later
defensive Result error branch unreachable through the outer function's tests.
That implementation reached only 5/6 branches and could not qualify as a library.

A revised probe seeds the fold with an error for invalid bounds and propagates
that error without inspecting rental occupancy. Both authored functions qualify,
and a fresh process confirms persistence. The outer function reaches 25/25
instructions and 6/6 branches; all 36 project tests pass. The tradeoff is traversing
the list even for an invalid window. This is a concrete way strict coverage can
shape composition, not evidence that the coverage rule guarantees correct behavior
or that this is the only possible implementation. Both attempts remain in the
evidence. No runtime primitive or qualification rule was changed.

## Verified controls

Preflight exposed harness defects before participant dispatch: the Flow fixture
used the wrong syntax version, its snapshot constructor used incorrect name
casing, and an unannotated F# formatter failed overload resolution. These failed
runs produced no behavior finding and are retained separately from corrected
controls. The corrected F# positive control passes 18/18 cases; the maximum-based
negative compiles and fails six aggregation cases. The missing F# target fails
compilation and has no behavior result.

The corrected Flow positive control passes all 18 logical cases (72 oracle
assertions) and its 36 inherited tests. Its source-compiled maximum-based
negative fails the same six aggregation cases; its 16 inherited baseline tests
pass. The negative is a source overlay in an isolated flat copy, not a claim
that a failing library was published. The absent Flow query fails fixture
definition and has no behavior result. All scored input projects are unchanged.
The preflight source review found no additional material defect. No participant
was launched before these controls and the input freeze.

## Participant outcomes

| Participant | Start | Required query | Own/inherited checks after grading | Broker exchanges | Error responses |
| --- | --- | --- | --- | --- | --- |
| flow-a | flat | absent after abort; behavior not evaluated | 16/16 inherited | 36 | 13 |
| flow-b | retained single-rental query | absent after abort; behavior not evaluated | 29/29 inherited | 34 | 17 |
| fsharp-a | retained single-rental query | 18/18 independent cases pass | 50/50 | 10 | 0 |
| fsharp-b | flat | 18/18 independent cases pass | 29/29 | 10 | 0 |

All four terminal audits pass normal `host.close`, with host/runtime exits 0/0.
All scored inputs remain unchanged. The missing AgentLang APIs fail oracle
definition, so their behavior results are null rather than 0/18 executed failures.
Flow-a briefly qualified an accumulator helper with five tests; task abort
removed it. Flow-b accepted no definition. Neither leaves a batch query or new
persistent vocabulary. Broker counts exclude the cross-participant messages and
coordinator clarification; full provider token usage is unavailable.

## Completed F# cells

Both F# participants pass 18/18 independent cases. The retained start finishes
with 50 own checks; the flat start finishes with 29. Each uses 10 broker exchanges,
receives zero error responses, and closes with host/runtime exits 0/0. Source
review confirms inherited APIs and tests remain, with additions confined to the
domain implementation and executable tests.

The retained participant preserves `billableDays` but repeats its clipping
calculation inside `totalBillableDays`; the batch function never calls it.
That is a correct submission without observed production reuse. Identical
interaction counts in these two cells do not establish a general cost effect.

## Discoverability defect and limits

Both AgentLang participants followed the prefix syntax advertised by
`describe list.fold`: `list.fold <word>`. Their Flow/2 requests repeatedly failed
with separator, trailing-input or unknown-call diagnostics. Required help.define
and help.examples responses did not show a fold example. The working form is
`items.fold(seed, callback)`, demonstrated by the preflight source and confirmed
by the parser/lowering review. Fold exists; the participants' conclusion that
the API was impossible in Flow/2 is incorrect. The runtime failed to make a
supported operation discoverable through the interface they used.

Both also tried recursion, which was correctly rejected by the existing acyclic
call-graph rule. This trial does not justify changing that rule or weakening
library coverage. The feasibility probe's coverage tradeoff is separate from
the participants' earlier syntax-discovery failure.

Flow-a asked flow-b for an accepted fold/traversal pattern; flow-b replied with
its failed forms and recursion result. Both reported this exchange. The
coordinator then prohibited further communication and preserved the assigned
Flow/2 constraint, without providing the working syntax. The messages can affect
search and abandonment even though no successful implementation was exchanged.
Their exact reported contents are saved in the results evidence. These two runs
are diagnostic observations, not independent replications. Future trial isolation
must explicitly prohibit sibling discovery/messages and restrict those tools where
possible. A repeat must be a separate study, not a replacement for these outcomes.

The retained F# participant did not reuse its prior function, and the AgentLang
participants produced no final batch implementation. This trial therefore provides
no positive evidence of a retained-vocabulary reliability benefit. It gives a
concrete next action: correct the fold descriptor and Flow help with executable
receiver-form examples, check nearby list constructs for the same mismatch, then
run a bounded fresh-agent discovery check. No broader runtime work is needed to
address this observed obstacle. A changed-help repeat cannot cleanly isolate
retention from discovery unless both factors are controlled.

## Evidence

The [preflight archive](evidence/147-agent-authored-batch-reuse/preflight.zip) and
[SHA-256 index](evidence/147-agent-authored-batch-reuse/preflight-index.json)
preserve 853 entries: prompts, exact starting projects, frozen runtime files,
oracle sources, controls, feasibility traces, failed attempts and source review.
The archive was written and every entry verified before participant dispatch.
It is 4,823,905 bytes; SHA-256
`86f6b53af30e85e19faae01108151a243df41756e7e380084b08c64b31091c7c`.

Post-trial hash audit confirms all 64 frozen non-project inputs unchanged. Final
source hashes, acceptance results, own-suite runs, terminal audits and independent
source reviews are retained with the participant traces in the results evidence.

The [results archive](evidence/147-agent-authored-batch-reuse/results.zip) and
[index](evidence/147-agent-authored-batch-reuse/results-index.json) contain 324
verified entries, 438,149 compressed bytes; SHA-256
`212f09d435f283444940bb5e8eb63156cc384fdbc3e60a54dfecf08656f7ce9a`.
The communication account is the archived
`.agentlang/batch-reuse-147/review/independence-incident.md`; final read-only reviews
are alongside it. The archive includes the exact participant prompts and grading
scripts, not just aggregate counts.
