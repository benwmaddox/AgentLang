# Retained I/O vocabulary follow-on

Both fresh submissions pass the specified twelve-scenario oracle. Only
F# reuses the retained safe-publication function. AgentLang discovers, reads and
tests it, then duplicates its logic. This is a negative observation for voluntary
vocabulary reuse despite successful behavior and library qualification. It
establishes neither comparative reliability nor a general performance difference.

## Task and isolation

Each participant starts from the saved accepted report-156 implementation using
the corrected report-157 validation and report-158 UTF-8 transport. Safely publish
an existing source to a distinct destination, then copy destination into a third,
distinct mirror unconditionally on Created/Unchanged. Conflict must preserve
both destination and mirror. Source and unrelated files remain unchanged. Empty
text, Unicode and CRLF must remain exact. Repeated calls consult current state.

Required per-call counts are Created: three reads/two writes; Unchanged: four
reads/one write; Conflict: three reads/no writes. Existence checking counts as a
read. Missing source and aliased paths are outside the contract. Both prompts
encourage reuse; it is an observation, not a hidden acceptance requirement.

One fresh Luna/max subagent per environment receives only its assigned prompt,
with no inherited conversation. Both report using the broker for discovery,
edits and tests, with only prompt reading and broker launch outside it. The
language target must qualify as library and commit its task. F# must preserve
existing functions/tests and pass self-tests. These are matched feature contracts
with different workflows, not identical authoring requirements.

## Independent results

The shared twelve-case matrix covers three outcomes across ASCII, empty, literal
Unicode and CRLF payloads, with absent or stale mirrors. Repeated success calls
also cover an identical mirror, which must still be written. Both saved
submissions pass all twelve scenarios.

Flow executes twelve single-call tests and twelve actual two-call sequential
tests: 24 tests, 36 target invocations. Single calls assert exact counts;
sequential tests assert both outcomes, final state, aggregate counts and two
target invocations. F# executes two sequential calls per case: 24 invocations,
with exact per-call deltas and complete file snapshots. Flow observes the four
named paths. Instrumentation differs; this is not a runtime-performance test.

| Observation | AgentLang | Conventional F# |
| --- | --- | --- |
| Independent behavior | 24/24 tests, 12 scenarios | 12/12 scenarios |
| Own tests | 5 new; 16/16 total pass | 3 new; 10 test functions pass |
| Retained safe-publisher reuse | No | Yes |
| Existing implementation preserved | Yes | Yes |
| Broker exchanges | 23 | 10 |
| Broker error responses | 3 | 0 |
| Broker session duration | 145.4 s | 80.9 s |
| Request payload UTF-8 bytes | 5,239 | 6,567 |
| Response payload UTF-8 bytes | 61,539 | 27,357 |

Durations run from broker start to close, excluding pre-launch prompt handling.
Bytes measure protocol traffic, not LLM tokens. Both traces record `host.close`
and host/runtime exit codes 0/0. No invalid UTF-8 errors appear. F# validation
passes with the remote NuGet vulnerability audit disabled; audit availability
itself was not tested.

The new language library covers 34/34 executable instructions, 4/4 branch
outcomes and all three enum returns in its own passing tests. Six own-test
invocations provide qualification independently of hidden checks. Publication
and task commit succeed. Original stored type, word and revision rows, including
attached test references, are unchanged. F# preserves the original operation
and test bodies and frozen infrastructure files.

## Discovery succeeded; composition did not

Flow successfully calls `describe`, `source`, `tests` and `test` for
`configuration.publish-safely`. Its final dependencies contain primitives and
enum constructors, but no safe-publisher call. It also reads the unconditional
publisher without using it. F# adds a short caller around `publishSafely`, using
an exhaustive match before writing the mirror on Created/Unchanged.

The language positive control calls the helper and passes the same oracle, so
reuse is technically possible. In a post-trial, tool-free debrief, the Flow
participant attributes duplication to focusing on explicit counts and calls it
an oversight. It reports no apparent type, effect, syntax, test or contract
obstacle. This retrospective self-report does not establish why the model chose
its implementation.

Flow's three errors are one invalid help request and two unknown-word lookups.
No submitted definition, test or publication attempt fails. Complete library
qualification can coexist with duplicated behavior: coverage and dependency
gates do not guarantee abstraction reuse or prevent vocabulary pollution.

## Controls and provenance limitation

Before dispatch, both positive controls pass and both arms reject conflict mirror
overwrite, incorrect mirrored text and a redundant unchanged-destination write.
These are behavioral rejections, not parser/build failures.

The original pin lists 168 artifacts. After dispatch, the oracle worker compacts
four Flow control reports and deletes their originals. Replacements are not
byte-identical. The original pin manifest remains: 164 artifacts match, including prompts,
seeds, runtimes, oracle code and cases; no pinned file has changed contents.
Four original control-output reports are missing. A fully verified frozen
evidence set cannot be claimed.

Coordinator reruns those four Flow controls against unchanged pinned code/cases.
They confirm the positive pass and three behavioral rejections, but do not restore
lost pre-dispatch byte provenance. Compact reports, fresh reruns and an explicit
provenance audit are retained together. Task and acceptance code remain unchanged;
the output loss is a material evidence-handling limitation.

The coordinator froze and dispatched after the worker announced successful
controls, while its final evidence cleanup was still unfinished. Future freezes
must follow the completed handoff, with no cleanup of pinned artifacts afterward.

Coordinator setup first used a wrong CLI capability option; control setup first
resolved a source directory incorrectly. Both were corrected before dispatch.
A preservation check initially used Windows-default Python decoding and then
passed with explicit UTF-8. These are coordinator failures. An early progress
update incorrectly attributed reuse to both actors; source/dependency inspection
corrected that claim before this report.

## Research consequence

This participant finds the tested function and still rebuilds it. F# reuses it
with fewer broker exchanges in this pair. One guided pair cannot estimate
success rates, establish causation or show that the language generally harms
reuse. It does counter the assumption that a typed dictionary naturally becomes
accumulated understanding.

Retain independent behavioral checks and library gates. Close this small I/O
sequence rather than collecting more similar successes. Future efficacy work
should use a materially different composition/maintenance problem and separate
voluntary reuse from instructions mandating a helper. Continue native
Option/Result conformance as the secondary engineering track.

The [evidence manifest](evidence/159-retained-io-vocabulary-follow-on/manifest.json)
records saved submissions, prompts, original pins, oracles, traces, acceptance
results, provenance audit and archive hashes. Validation is local; CI remains
manual-only. Product runtime semantics are unchanged.

The archive contains 275 entries, 4,143,715 bytes, with SHA-256
`0e78b30093991b1fb7f0c8aa0cbaa09c9e977e6fd3124abcec875dfeb9897fd7`.
