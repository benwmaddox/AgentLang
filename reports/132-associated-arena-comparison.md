# 132 — Compare attached working arenas with return at suspension

Status: focused native comparison and broader local regressions pass.
No throughput result is claimed.
The starting revision is `ee76cd1`.

Keeping the working arena attached avoids the expected mid-suspension copies
and preserves exact results and failed-resume retries. It also pins scarce
scratch slots: two waiting operations occupy the two-slot pool and block a
third begin, while the return policy admits it. This establishes the mechanism
and its tradeoff, not a performance winner.

Final policy run `ae65e9dd23c646ffb684d5819e6fc4f9` passes **234/234 verifier
checks and 97/97 native assertions at each of O0/O2**. The ordinary mailbox
regression run `508a40f625d44b22bdde77a3e58d6487` passes **198 checks**. Both
record unchanged inputs; an independent current-file check matches all 21 policy
and 22 ordinary recorded source hashes.

Broader regressions pass the owning-stack verifier (35 checks,
run `1ea4d6effa6342358efa5e8a33609368`) and native dispatch verifier (353 checks,
run `5f8f7cf87a594ef8ba8d63ebcd148788`). The full Release gate passes all 37
checks, including the isolated business-policy controls; the separate established
LLVM regression suite passes 476 assertions. The four-step regression sequence
completed with exit zero throughout on 2026-10-08 (20:03:26–20:39:43 UTC).
All validation is local; the Release gate
uses `-SerialBuild -SkipPackageAudit`, so package vulnerability auditing is not
part of this run.

Report 131 validates native owning mailbox turns that publish State and
Continuation into retained banks before releasing scratch at suspension. This
milestone adds the requested alternative: keep the actual working arena and its
result locations attached until the operation group completes. Both policies
use the same compiled handlers and configured scratch-pool reservation.

The acceptance gate compares behavior and memory traffic, not throughput.
It covers two interleaved pending mailboxes, exhaustion before token
consumption, unchanged pending data after failed resume or publication, retry,
and final state equality at O0/O2. Associated resume imports only the new
completion message, with no intermediate publication or root reimport. Failed attempts
discard appended temporary data and preserve the pending prefix without
a backup copy. Final successful state publication remains an explicit copy.

The Unicode/empty lifecycle matches these source-derived expectations at both
optimization levels:

| Counted payload traffic (bytes) | Return at suspension | Keep associated |
| --- | ---: | ---: |
| External input imports, including retained roots | 152 | 104 |
| Retained-bank publication | 112 | 64 |
| Construction/duplication | 184 | 184 |
| Payload relocation | 0 | 0 |

The 48-byte reduction in each of the first two rows is the State/Continuation
pair across the Unicode and empty
cases. Completion text is still imported, and initial/final State is still
published. UTF-16 staging is 80 bytes under either policy, counted separately.
Both configurations reserve 247,632 caller-owned bytes. Scratch cursor peak is
144 bytes under RETURN and 160 under KEEP; fewer boundary copies do not imply
a smaller working extent.

The pool-exhaustion case uses three initialized mailboxes and two scratch slots
under each policy. Two suspended KEEP operations pin both slots; a third
begin rejects before handler execution or token consumption. RETURN
reuses a released slot. Equal configured storage is not equal occupied storage;
the result records this admission difference.

In that failure/retry case, imports are 376 bytes under RETURN versus 216 under
KEEP, and publication is 192 versus 96. Construction is 416 bytes and relocation
zero for both. Both reserve 182,648 caller-owned bytes, including 163,840 scratch
and bitmap bytes. KEEP pins 131,072 arena-capacity bytes while two operations
wait; this excludes bitmap/controller storage and is not whole-process RAM.

Persistent mailbox state remains bank-owned between operation groups. The
implementation keeps the existing lifecycle controller and compiler-proven
rewinds; it adds no source lifetime annotations or runtime liveness analysis.
An attached arena can also retain dead interior temporaries below surviving
values; this policy does not compact them at suspension.

The compiler project builds in Release with zero warnings or errors. Independent
source review of the active dynamic emitter finds that locals, projections and
calls transfer descriptors, while construction appends after the saved cursor.
Resume failure cleanup uses marks at or above that cursor. This supports the
protected-prefix design for the currently supported inline values; the focused
native failure/retry tests now exercise that design.

Integration review found one accounting regression before native execution:
the refactored ordinary path counted returned descriptors only after successful
bank publication. The correction counts successful callback outputs even when
later publication fails, preserving report 131's metric and expected counts.

The pool-layout review also found an unaligned slot stride for capacities whose
bitmap sizes are not alignment multiples (for example, a 72-byte arena). The
second slot would be misaligned even with an aligned caller buffer. The backing
base also needed alignment after an arbitrarily sized text-staging region.
Both are now aligned with checked arithmetic, and the direct regression covers
both unusual capacities. Padding remains included in total reserved storage.

Direct native controller checks now pass at O0, O2 and with trap-based
undefined-behavior instrumentation. They exercise two attached slots, a 72-byte
arena with a 37-byte text-staging region, no-slot rejection, prefix-preserving
failed resume, retry and release. The existing graph controller also passes
its 70 checks at each of O0/O2. These component tests use direct C callbacks;
they remain separate from the integration test of LLVM-generated handlers.
The twelve recorded component source inputs match before/after hashes and the
current files in an independent check.

The first integrated attempt (`c29be5bedfca451c84ec61057e7c3b0a`) stopped at
O0 module linking: the generated associated preflight's descriptor copy
introduced an unresolved `memcpy` dependency. No native acceptance case ran.
The correction stays within generated descriptor loading rather than expanding
the runtime's external dependencies; subsequent fresh module builds pass.

The next ordinary integration run (`3b9e55916b954c6181091777431f2007`) passes
198 verifier checks. The first policy run (`0ee4cb81069a4eaa8d8a6d5c2a9145e7`)
then reaches O0 native execution: its lifecycle checks pass, but ten direct
callback checks fail or cascade from failed fixture setup. Source review finds
that the fixture copies initialized State out, then invokes ordinary begin
without resetting the scratch cursor as that callback requires. This setup
correction must not reset the real arena retained for associated resume. The
attempt remains a failed policy gate; it is not counted as acceptance.

Two subsequent verifier failures are retained separately: `d17dd81...` had a
PowerShell helper nested in the wrong scope after O0 native success;
`a12b327...` passed all 97 native assertions at each of O0/O2 but used the
adversarial case's 32-byte bank reservation when checking the separate paired
case's 16 KiB banks. The fixture now declares reservation inputs per case and
records this post-execution correction. Behavioral expected bytes and copy
totals did not change. The final fixture is not represented as an untouched
pre-first-execution oracle.

Configured storage, occupied/pinned scratch, imports, construction, publication,
and relocation must remain distinct. The existing native probe includes poison
writes and initialization instrumentation; its elapsed time is not evidence for
a release-runtime throughput claim. Actual bounded I/O, cancellation ownership,
tail latency and whole-process memory need a subsequent matched comparison.

The agent-efficacy conclusion remains report 125: discoverable reuse and enforced
gates work in the tested tasks, but reliability superiority over F# is unproven.
This storage comparison answers a separate runtime-design question.

The [evidence index](evidence/132-associated-arena-comparison/storage-index.json)
describes the [archive](evidence/132-associated-arena-comparison/132-associated-arena-comparison.zip):
641 payload files plus the internal manifest, including the failed attempts,
fresh generated modules, final gate results, component checks and 24 source
snapshots covering the union of final gate inputs. Independent verification
matches every entry's byte length and hash, the archive/index/manifest hashes,
the ordinal-path payload digest and all 24 current source hashes. The archive
is 8,040,518 bytes; SHA-256:
`e15cd2ac7be146ad6d4f76ffb2153b99d7cdb78bf8f5c26211c0c53d9ac9a76e`.

Next, use a bounded steady-state fixture and actual socket completions rather
than repeatedly extending this fixture's transcript. Provider-acknowledged
cancellation and a validated release reset path are necessary before comparing
sustained throughput against F#; see the
[next bounded implementation](../docs/ASYNC-ARENA-EVALUATION.md#next-bounded-implementation).
