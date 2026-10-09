# Rental query: discovering retained interval vocabulary

Status: preflight verified and frozen; four independent participants dispatched.
No participant efficacy results have been graded yet.

## Question and design

Report 143 found a real empty-interval defect despite complete declared library
coverage. Report 144 demonstrated a validated nonempty interval abstraction in
both AgentLang and F#, but coordinator implementation is not evidence that a
later agent will discover or use it. This comparison asks an unfamiliar agent to
add a rental billing query, with and without that retained vocabulary.

Four independent Luna/max participants receive the same public behavior contract:
one per language and retention condition. Each has at most 100 broker exchanges.
The task does not name the interval helper or require its use. Each language's
retained start differs from its flat start only by interval vocabulary and its
associated tests/project wiring. This is an exploratory four-cell comparison,
not an estimate of population reliability or a causal demonstration of retention.

The query returns the number of occupied integer rental days inside a half-open
requested window. Stored rentals span at most days 0 through 365. Cancellation
may leave an empty occupied interval or occur after the original finish. Invalid
request windows must be rejected before considering empty occupancy. Request
bounds may span almost the full signed 64-bit range, so implementations cannot
safely subtract the unrestricted request endpoints.

The independent oracle enumerates the bounded stored rental days and filters
those days by the request and cancellation. It does not reuse the candidate's
interval-intersection arithmetic. Seventeen cases cover clipping, containment,
adjacency, cancellation, empty occupancy, invalid windows, extreme bounds and
input preservation. Positive controls must pass; a compiling positive-count-plus-one
control must fail on positive counts while preserving zero/error cases. Absent
query controls test the missing-entry path, not behavioral correctness.

## Qualification and comparison limits

AgentLang requires the new query and its authored production dependencies to
qualify as libraries. F# uses its ordinary compiler and test workflow. These are
deliberately different development environments, not equivalent proof systems.
Existing tests and APIs must remain. Both F# starts disable NuGet audit before
launch, removing the network-dependent audit failure observed in report 143.

The frozen runtime refuses library qualification for fixture builders returning
validated Rental because its Option<Int> field has unproven finite projections.
Those builders remain project-level; validation and cancellation semantics are
preserved. A separate positive query probe qualified successfully and passed a
fresh reload. Its Rental input is not a direct Bool/enum input coverage obligation,
and its Result<Int,RentalError> return is supported. Calling project-level
builders from attached test setup does not put them in the query's production
dependency closure. No coverage gate or runtime implementation was changed.

Consequently, library qualification does not establish exhaustive Rental input
coverage. Independent behavior checks remain necessary. Reuse is recorded only
when the submitted production call graph reaches vocabulary present at launch;
an agent-created helper in a flat start is newly authored abstraction, not retained
reuse. Correctness, inherited regressions, library maturity, scope, broker
completion and reuse are separate findings. The retained helper supplies validated
construction and overlap detection, not intersection length. Direct bounded
arithmetic may be a sensible solution; non-use in this task would not establish
that accumulated vocabulary is generally unhelpful.

## Verified preflight

Flat and retained AgentLang starts fresh-reload at 16/16 and 24/24 tests.
Their inherited function IDs and sources match. F# starts pass 16 baseline
checks and 16 baseline plus 7 retained-interval checks respectively. Their
Rental implementation, baseline tests and README match byte-for-byte. The query
is absent from all four starts. The correct language query qualifies as a
library and fresh-reloads at 31/31 tests.

The final eight oracle runs use scorer SHA-256
`e97a6f1874ff8113149cef32a468e672cb3ea1a144b2d45abfb81ed89e202a1f`:

| Control | AgentLang | F# |
| --- | --- | --- |
| Correct query | 17/17 | 17/17 |
| Positive count plus one | 8/17; nine positive counts fail | 8/17; nine positive counts fail |
| Flat start, query absent | No behavior claim; missing function | No behavior claim; missing function |
| Retained start, query absent | No behavior claim; missing function | No behavior claim; missing function |

All eight runs preserve input project bytes. The language mutant compiles and
runs as a staged source overlay with unchanged correct assertions; it is not a
published library. The scorer records the source hash and checks it is unchanged.
The negative result is a wrong-answer rejection, not a parser/load failure.

Setup attempts are retained, including language source/version corrections and
an initial scorer that needed to distinguish missing targets from evaluated
cases. An attempted F# `dotnet test` check did not execute the custom assertion
program; the final scorer leaves inherited-suite status null. The fixture's
separate executable runs provide start-suite evidence. Participant suites will
also be executed as programs, not inferred from `dotnet test` exit status.
These coordinator corrections are not participant failures.

## Frozen evidence and next result

The 161 hashed inputs include the task, prepared project inventories, prompts,
oracle and both frozen runtime artifacts. All four start copies match their
source inventories. The archive was created and every entry hash verified before
any participant was dispatched. Each participant receives its own prompt hash,
no conversation history, and only its assigned broker/project access.

[Preflight archive](evidence/145-rental-vocabulary-reuse/preflight.zip) and
[entry index](evidence/145-rental-vocabulary-reuse/preflight-index.json):
2,183 entries, 5,957,053 bytes; archive SHA-256
`30773bc9b6d7197c69887824dd767bb11835419304ea0c77feb5d38db93fef5b`.
Raw materials are retained under `.agentlang/reuse-145/`. The archived
prepared-not-dispatched status describes the freeze checkpoint; dispatch occurred
afterward. Final submissions, independent grading and actual reuse remain pending.
