# Efficacy of the inspectable typed-vocabulary approach

Status: evidence review completed, 2026-10-07. The practical mechanism works;
comparative reliability remains unproven. This is the primary research finding,
not a claim that the full PRD or native runtime is complete.

## What the evidence supports

Fresh external coding agents can inspect capabilities through the runtime,
compose strongly typed operations, attach tests, satisfy current library gates,
commit definitions and leave reusable vocabulary. They have actually discovered
and called abstractions created in earlier tasks. The latest four-condition
comparison preserved the starting definitions and independently accepted the
behavior of every output. Conventional F# was equally correct.

| Evidence | Verified outcome | Interpretation |
| --- | --- | --- |
| [Repeated five-task comparison, report 064](064-repeat-agent-purpose-review.md) | All 15 trials accepted; each of three arms succeeded on all five small tasks | Feasibility and composition; a correctness ceiling prevents a reliability advantage claim |
| [Original sparse S07, report 084](084-retention-study-flat-discount-result.md) | 42/54 independent cases, despite 8/8 own tests, 20/20 instructions and 2/2 branches | Coverage and agent-written assertions can qualify an incorrect business rule |
| [R04 supplemental result, report 090](090-retained-discount-supplemental-scoring.md) | Saved output passed 54/54 cases in separately scored scratch copies | Correct saved behavior and reuse; original frozen acceptance remains blocked |
| [Quick four-condition comparison, report 099](099-quick-agent-comparison.md) | Sparse, retained, reset-rich and F# each passed 54/54 cases | All conditions can solve this task; no reliability superiority demonstrated |

The retained quick-trial function calls `customer::premium?` in its
[saved final source](evidence/099-quick-comparison/source/actors/retained/dictionary.agent).
It preserved 54 prior authored words and 31 nominal types. Reset-rich preserved
53 words/31 types; sparse preserved 0 authored words/6 types. Reuse also occurred
in the conventional repeated comparison, so composition is not exclusive to
this language.

The independent efficacy reviewer recomputed the saved R02 and latest-language
outputs using signed BigInteger arithmetic and ordinal raw-kind comparison.
The [R02 acceptance](evidence/084-R02/run/acceptance.json),
[latest language summary](evidence/099-quick-comparison/results/language-summary.json)
and [F# probe result](evidence/099-quick-comparison/results/conventional-behavior.json)
retain those behavioral boundaries. Do not aggregate different protocols into a
single experimental success rate.

## What remains unproven

We have not shown fewer incorrect accepted changes, fewer unrelated regressions,
better diagnosis of planted defects, or a growing reliability advantage as the
dictionary matures. No controlled small-context result is available. Retained
made 58 runtime exchanges in the quick run, versus sparse 33 and reset 43.
Those observations show no call reduction; they do not measure model tokens or
controlled latency. The repeated study also found conventional helper reuse.

Every latest language actor initially created an noncanonical Instant
fixture. Others corrected constructor-name guesses, malformed calls, integer
request generation or endpoint expectations. Structured diagnostics permitted
recovery, but these cases expose discovery and authoring friction. They do not
demonstrate superior recovery from business defects.

R02 is especially instructive: subtracting truncated 10% from the original
balance differs from truncating the final 90% balance. Its own tests encoded the
wrong expectations while still executing every branch. Full coverage is a
useful publication requirement, not proof of the specification. Independent
acceptance, meaningful boundaries and preserved regression tests remain vital.
Finite input/output qualification, stricter library-call closure and injected
effect contracts remain planned work; current results cannot imply those gates
already exist or are effective.
The newly clarified test-local dictionary overrides are also a planned mechanism:
tests can substitute IO functions and discard those substitutions afterward,
with type/effect checks and automatic cleanup. This may make isolated behavior
tests easier to author, but no efficacy result for it is available yet.
The dictionary binds implementations through checked identities and contracts.
When JIT exists, an override must invalidate transitive callers and inlining;
interpreting the affected closure temporarily is the initial safe approach.
Release AOT can still pin the graph and optimize direct calls.

The latest actors omitted documentation or attached examples required by the
older full-study verifier. Behavioral and current library checks passed, but
those outputs are not full original-study acceptance passes. Report 099 records
the distinction and the separate F# typed-probe scoring. R04's successful
supplement is similarly separate from its blocked original protocol.

## Assessment and next useful test

The strongest demonstrated value is an explicit, inspectable unit of project
knowledge with a bounded editing and publication interface. The language can
enforce its type/effect and current structural-testing contracts, and agents can
reuse the resulting vocabulary. Whether that makes reliable changes easier than
a well-designed typed repository remains an open question.

Keep the next experiment small and target reliability rather than another clean
task with universal success. Seed the observed R02 rounding defect and its
passing incorrect expectations in matched retained, reset-rich and conventional
projects. Ask fresh actors to repair signed rounding while preserving unrelated
behavior. Reuse the existing hidden S07 cases, regression checks and trace tools;
record whether implementation and expectations are corrected, incorrectly
qualified edits, gate rejections and regressions. One actor per condition is
still exploratory, but it tests an unresolved failure mode directly. No new
large harness is a prerequisite.

Flow/2 addresses the user's authoring preference and observed interface friction.
Its implementation tests are not evidence that agents perform better with it;
that requires an actor trial. Further features should either test the reliability
hypothesis or advance the secondary execution target below.

## Secondary execution target

The second priority is efficient execution through the same authoritative typed
semantic IR: development interpreter plus LLVM JIT, and LLVM AOT release builds
with a minimal runtime. F# may remain the compiler/host implementation. This is
an intended target, not an implemented backend or measured memory result.

The main memory candidate is per-turn scratch arenas, explicit retained mailbox
state, bounded pending I/O and same-mailbox resume. At suspension, surviving
values and resume state must become valid retained state before the scratch
arena returns to its pool; resumption reacquires scratch storage. Specify and
verify escapes, returned values, cancellation/resource cleanup, stable mailbox
identity and executable-generation lifetimes before adoption. Compare against
whole-request arenas under the same memory ceiling and acceptable tail latency,
including slow clients and cancellation. Use observed footprint and sustained
throughput to choose the design; stack notation or LLVM alone proves neither.

Start with a narrow native conformance slice and actual allocator measurements,
not a full application migration. Native interpreter/JIT/AOT results must agree
on checked arithmetic, nominal identity, errors, effects and value lifetime
semantics. The Campfire migration stays after a stable implemented native
runtime and chosen memory design.

Follow-up: [report 102](102-guided-defect-repair-comparison.md) completed the
small guided repair comparison. All three conditions improved from 42/54 to
54/54 independent cases, with preserved unrelated behavior. This adds repair
feasibility evidence and still does not establish comparative superiority.

Execution follow-up: [report 103](103-llvm-architecture-and-native-slice.md)
validates the first bounded scalar LLVM AOT backend. General native release
support, JIT and language-level arena lifetimes remain incomplete. Native
conformance does not change the comparative agent-reliability conclusion above.
