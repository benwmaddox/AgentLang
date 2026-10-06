# 061 — Repeated external-agent comparison: discount task

Status: partial repeated sequence, 2026-10-06. Task 2 introduces the first
opportunity to reuse task 1's accepted predicate. The same frozen `dc18d6e`
runtime and five public tasks remain in use. Order is Growing, Flat, Conventional;
each task uses a fresh zero-history GPT-6 Luna/max agent.

## Task 2 outcomes

Premium customers receive balance × 0.9; other customers retain their balance.
Independent acceptance tests the same 20 behavioral vectors in each arm.

| Arm | Independent checks | New own tests | Exchanges | Request bytes | Response bytes | Runtime errors |
|---|---:|---:|---:|---:|---:|---:|
| Growing | 48 passed | 4 | 14 | 1,442 | 8,837 | 0 |
| Flat | 48 passed | 5 | 17 | 2,690 | 12,483 | 2 |
| Conventional | 43 passed | 6 | 9 | 1,798 | 7,681 | 0 |

All three arms passed task 2: 60 behavioral evaluations and 139 independent
checks. Six of the repeated sequence's 15 trials are now accepted; nine remain.

Growing inspected `customer.premium?`, then called it in the new
`customer.discounted-balance` definition. Its passing own tests cover 9/9
instructions and both branch outcomes. The previous word's ID, current head,
complete revision metadata, definition/test object hashes and both type entries
are unchanged. The final dictionary contains two library words. Independent
behavioral acceptance passed before the state was retained for the next task.

This establishes discovery and reuse by a later fresh agent in this small
domain. It does not establish reduced model cost, successful small-context work,
or full PRD success. Byte totals count protocol payloads, not model tokens;
exact model usage and controlled duration are unavailable. Different self-test
counts and the library publication coverage policy limit cost comparisons.

Flat's initial definition used correct qualified calls in its body but dotted
dictionary-name calls in attached tests, for example
`customer.discounted-balance(...)`. Flow interprets that spelling as a receiver
call on a local `customer`; the tests had no such binding. Validation rejected
the whole document with `FLOW_UNKNOWN_LOCAL`, and the following describe request
confirmed the word had not been staged (`NAME_UNKNOWN_WORD`). This is an observed
call-spelling recovery case, not an accepted definition or a silent partial edit.
It suggests testing a deterministic diagnostic hint for receiver/namespace
confusion after this frozen comparison, without changing the apparatus mid-run.

Flat recovered by spelling the attached test calls `customer::discounted-balance`.
Five own tests passed with 11/11 instructions and 2/2 branch outcomes covered.
Independent acceptance passed all 48 checks. Its final state contains one
library word and unchanged schema types; no prior domain vocabulary was supplied.
The 9-instruction Growing versus 11-instruction Flat definitions show a small
own-definition compression, not reduced transitive work or allocation.

Conventional also reused its existing `Customer.premium` function. The exact
source audit verifies only the requested `discountedBalance` implementation
changed in Domain.fs; records, prior predicate, future entry points and project
file are unchanged. All six earlier self-tests remain intact, with six added
discount cases. Public validation passed with 12 total cases, followed by 43
independent task-2 checks. No runtime errors occurred. Conventional needed fewer
exchanges than either language arm on this task; reuse is not unique to the
language. These observations do not demonstrate an overall language efficiency
advantage.

## Evidence

The [repeat artifacts](../experiments/AgentLang.SubagentTrials/early-flow-002/runs/repeat-001/)
include prelaunch intended prompts, starting-state hashes, source/executable pins,
raw traces, acceptance, metrics and final source/storage. Trial agents received
only their selected public task and interface primer; they had no acceptance
oracle or sibling source access through the protocol. Existing host/session
cleanup remains a separate coordinator follow-up, without extra JSONL requests.
Provider-internal prompt bytes and exact token/turn usage remain unavailable.

Coordinator audit passed 86 wire/provenance checks across 40 exchanges and pins
68 task-2 artifact files. Exact staged-byte and diff checks precede publication.
One coordinator read-only trace inspection hit an approval-review deadline;
the permitted single retry succeeded. This is recorded separately from the
agents' runtime errors. All existing hosts stopped cleanly with exit code 0.

The previous publication is independently verified: [main CI identity](evidence/060-main-ci.json)
and [validation](evidence/060-main-validation.json) show run 37500245538 passed
all 32 checks on clean `c0cd9dbd4bb10435d628bb34e1f885cf43ca09a7`. This report
and its trial evidence do not change runtime source or semantics.
