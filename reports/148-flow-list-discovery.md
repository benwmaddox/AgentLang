# Flow list discovery: correct the advertised call forms

Status: implementation, local validation and the single-participant follow-up
are complete. The submission passes 18/18 oracle cases and its 36 own tests.
It uses the supported fold syntax but duplicates the retained billing calculation
instead of leaving a production call to the earlier function.

Report147's AgentLang participants could not discover the supported fold form.
The syntax descriptor advertised Stack's `list.fold <word>` while Flow/2 expects
`items.fold(seed, callback)`. A coordinator control could implement the task,
but the agent-facing interface failed to make that capability usable.

## Change

List map, filter, each and fold descriptors now include a separate machine-readable
`flow2Syntax` property. The existing Stack `syntax` strings remain unchanged.
Search indexes the receiver forms, and descriptions state that callbacks are
statically named functions and cannot capture caller locals.

Flow/2 define and examples help include a generic integer-sum fold with a named
callback, empty and populated tests, an attached example, and ready-to-run
define/test/example/eval requests. The example contains no rental-specific code.
Tests execute the help-returned requests through the protocol and check the
advertised map/filter/each receiver forms. No grammar, semantic IR, execution
rule, effect rule or library qualification requirement changes.

## Local validation

- Clean serial Release solution build: zero warnings and errors.
- Flow Runtime executable: 32 groups, 1,140 assertions pass.
- CLI executable: 11 groups, 145 assertions pass.
- Fresh isolated CLI build: zero warnings and errors.
- Unchanged report147 oracle against its correct control using the fresh CLI:
  18/18 cases pass; input unchanged.
- Read-only review found no material issue; diff checks pass.

Commands and full output are preserved. Initial development attempts caught a
missing F# record delimiter and an overly specific prose assertion; those logs
remain separate from passing validation. No CI ran. Tests of the returned syntax
are acceptance evidence for this change, not a new proof of iteration semantics.

## Follow-up design and limits

One fresh Luna/max participant receives report147's same public batch-billing
contract and retained starting dictionary, with 100 broker exchanges. The oracle
is copied unchanged and the prompt gives no fold syntax or implementation hint.
The runtime and inputs are frozen before dispatch. Correctness, library status,
inherited tests, production calls to retained vocabulary, errors and normal broker
closure are assessed separately below.

The prompt explicitly prohibits listing, inspecting or messaging other agents,
delegation, and out-of-broker project work. Only one participant runs. The available
agent API does not provide a per-participant tool whitelist: these are prompt
constraints, not tool-enforced isolation. Completion must disclose any external
tool use or information received. Any observed violation remains in the record.
The general host guidance now makes this limitation and incident handling explicit.

This is a separately identified discovery follow-up, not a replacement for the
contaminated report147 observations or a new four-cell comparison. Even a correct
submission would establish only bounded feasibility after correcting guidance;
one retained participant cannot measure a general retention benefit.

## Fresh-agent result

The participant completes in 35 broker exchanges with four error responses:
three source-syntax errors and one callback accumulator-type mismatch. It finds
the supported receiver fold form, publishes the batch query and accumulator
helper as libraries, and finishes with normal task commit and host close,
host/runtime exits 0/0. It reports no other tool or information use beyond prompt
verification and the broker. No cross-participant exchange was observed or reported.

The two required help responses total 20,672 UTF-8 payload bytes, versus 15,311
for report147's retained participant: the additional guidance costs 5,361 bytes
at that step. These are measured broker payloads, not LLM tokens or total context.

The unchanged independent oracle passes all 18 cases and confirms the input
project was not changed by scoring. The final own suite has 36 passing tests:
29 inherited and seven new. The target covers 20/20 instructions, 4/4 branches
and both Result alternatives; its helper covers 43/43 instructions and 4/4
branches. These are executed coverage obligations, not proofs over all inputs.

The final source adds one accumulator record and two library functions. The
earlier `rental.billable-days` remains unchanged, but is absent from the final
query's production dependency closure. The new helper repeats its clipping
calculation. An abandoned candidate helper was discarded before task commit.

## Attempted reuse and coverage interaction

This participant initially does reuse `rental.billable-days`. The staged batch
query's transitive dependencies at exchange18 include it; the callback's three
tests and query's four tests pass. The same describe response reports only 5/6
query branches and 23/25 instructions: the post-fold error case was not executed.
The outer function rejects invalid windows before the fold, so the retained
query's invalid-window error cannot be reached on that path.

The participant then replaces the callback with direct arithmetic, eliminates
the Result accumulator, and qualifies the revised functions with full coverage.
The trace establishes that sequence, not the participant's private reasoning.
It is consistent with coverage requirements influencing the move away from reuse.
No failed library commit was necessary: the coverage gap was visible before
publication. The final implementation preserves early invalid-window rejection.

The report147 coordinator demonstrated another legal solution: seed the fold
with an error and traverse the list while propagating it. Therefore the gate
does not make reuse impossible, but the examples expose a composition cost.
The correct conclusion is narrower than a vocabulary success: the discovery
obstacle was overcome, a correct tested change was produced, and attempted
reuse did not survive into the final implementation.

A useful next research question is whether strongly typed validated inputs let
domain calculations compose without repeatedly returning errors that the caller
has already ruled out. Compare that API shape while preserving library coverage
requirements, rather than counting this duplication as reuse or loosening gates
to obtain a passing experiment. One successful follow-up does not establish a
causal improvement rate from the help change.

## Evidence

The [preflight archive](evidence/148-flow-list-discovery/preflight.zip) and
[index](evidence/148-flow-list-discovery/preflight-index.json) contain 172 verified
entries, including changed sources, executable tests, runtime binaries, help
review, frozen prompt/start, oracle and control results. Size: 2,769,819 bytes.
SHA-256: `231fe3f982558d48b6a9f09b618e881a78e256ddae469f1a7a5be9e843453ef7`.

The [results archive](evidence/148-flow-list-discovery/results.zip) and
[index](evidence/148-flow-list-discovery/results-index.json) preserve 163 verified
entries, including the final project, broker trace, scorer output, library and
dependency inspection, terminal audit, tool-use disclosure and read-only review.
Size: 258,562 bytes. SHA-256:
`dab867981bc5484dc62584d03868e4f61ba10c08b64c2a34231be9fc975308fb`.
Frozen non-project inputs and final sources were hash-checked before packaging;
the final source state matches the one graded.
