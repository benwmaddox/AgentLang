# Batch billing: reuse of an earlier agent's domain function

Status: controls validated and 157 inputs frozen before dispatch. Four independent
participants have been dispatched; efficacy outcomes are pending.

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

## Outcomes to record

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

Record independent correctness, inherited-test preservation, library qualification,
actual retained production calls, newly introduced helpers/types, protocol errors,
normal broker termination and supplied context separately. Do not infer token
savings without provider token usage. Participant outcomes remain pending.

## Evidence

The [preflight archive](evidence/147-agent-authored-batch-reuse/preflight.zip) and
[SHA-256 index](evidence/147-agent-authored-batch-reuse/preflight-index.json)
preserve 853 entries: prompts, exact starting projects, frozen runtime files,
oracle sources, controls, feasibility traces, failed attempts and source review.
The archive was written and every entry verified before participant dispatch.
It is 4,823,905 bytes; SHA-256
`86f6b53af30e85e19faae01108151a243df41756e7e380084b08c64b31091c7c`.
