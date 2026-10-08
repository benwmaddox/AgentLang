# 131 — Owning values through native mailbox turns

Status: locally validated. Focused integration, native regressions, the full
Release gate and the established LLVM suite pass. No performance result is
claimed. The starting revision is `c583cd3`.

Report 130 validated stable arena payloads and compiler-proven rewinds in the
bounded owning backend. This milestone connects that representation to the
existing native mailbox lifecycle controller, without routing it through the
older graph representation or the managed execution adapter.

Fresh focused run `8fcf8a73ff8c4f0fa2679309533ef25e` passes **180/180 verifier
checks and 122/122 native assertions at each of O0 and O2**. All 22 recorded
compiler/runtime/fixture/harness inputs stayed unchanged during the run, and a
separate current-file hash check matches all 22. The generated DLLs have no
allocator or CLR imports. Fresh runtime-object symbol audits find no direct
allocator references; the harness executable's CRT heap imports remain recorded.
This is a static allocation-boundary check, not a process-wide allocation trace.

Fresh regression runs also pass the existing owning-stack verifier (35 checks,
run `493f79e15a91482da96b563b6b14df99`) and graph/native dispatch verifier
(353 checks, run `a51a225d8efa4f8ba9965e3de92ced6b`). Both report stable source
inputs. The full Release gate passes all 37 checks, including 98 business-policy
checks across 30 independent outcomes. It ran from 14:51:44 to 15:21:23 local time
on 2026-10-08 with `-SerialBuild -SkipPackageAudit`. No CI was used; package
vulnerability auditing was disabled for this local run. The established LLVM
regression suite also passes all 476 assertions using that fresh Release build.

The two-mailbox fixture reserves 165,376 caller-owned bytes: 65,696 retained-bank
bytes, 81,920 scratch/bitmap bytes, 16,384 text-staging bytes and 1,376 controller
bytes. These are configured capacities, not the memory needed by its tiny
payloads, and exclude native stack and whole-process overhead.

Its six successful lifecycle calls import 152 bytes, construct/copy 184 bytes,
publish 112 bytes and relocate zero payload bytes. UTF-8 conversion produces
80 staging bytes separately. Both optimization levels agree. The 4 KiB request
and retained-capacity failure/retry scenario also records zero relocation;
it still performs explicit imports, construction and publication copies.
Ordinary descriptor movement must not be confused with those boundary copies.

## Selected integration

- One generated module exposes initialize, begin and resume entries from the
  same verified program, sharing a deterministic layout/type-index universe.
- Entries import bounded input slices and return result locations into their
  still-live working arena. They do not publish outputs or reset successful
  scratch themselves.
- The existing controller stages those results once into an inactive retained
  byte bank, publishes the complete set, and then resets scratch. Token,
  generation, thread-confinement and failure rules remain shared with the graph
  control. No second mailbox controller is introduced.
- Native input ranges, aggregate extents, capacities and metadata are checked
  before inspecting payloads. External UTF-8 conversion, scratch input imports,
  construction, publication and descriptor traffic are reported separately.

The first path returns scratch at suspension after retaining all needed state.
Keeping the actual scratch arena associated while pending remains a required
comparison. Neither is the selected performance winner. Real asynchronous I/O
ownership and a matched F# service workload remain subsequent acceptance work.

## Acceptance and evidence boundary

The integrated O0/O2 test uses dynamic String requests, including a request
larger than the earlier tiny fixtures, and an independent byte/behavior oracle.
It exercises two interleaved mailboxes, reset/reuse, stale/cross-owner tokens,
handler failure, scratch/retained capacity failures, and unchanged active state
and pending token on failure. It accounts for the single retained publication
copy and caller-reserved storage regions. The existing graph mailbox remains a
regression control; the full local Release gate is required before publication.

A separate storage draft passed 44 direct C checks at O0/O2 and received a
read-only safety review before integration. That is component evidence only.
A zero-width projected Empty value must be materialized by the generated entry
as its canonical standalone token before bank publication. This draft's checks
do not establish native mailbox correctness or throughput.

The latest agent-efficacy conclusion remains report 125: discovery and reuse
were observed, but comparative reliability superiority over F# was not shown.
This native integration does not change that research result.

## Integration findings before acceptance

The compiler project builds in Release with zero warnings or errors. The first
integrated attempt stopped while compiling generated C: its exported module
getter declaration disagreed with the shared header under strict Clang warnings.
That declaration has been corrected; this attempt ran no native acceptance tests.

Independent source review also found a String header unit mismatch (byte extent
instead of UTF-16 code-unit count), a type-index bounds check ordered after a
descriptor access, and an exported callback overlap gap involving its type
metadata. The source corrections are in place and exercised by the passing
integrated run. They are implementation defects found
during this milestone, not evidence that language-level agent edits are more
reliable than F#.

The final controller reset metric is named `turn_reset_bytes`: it measures the
cursor reset at turn completion, not compiler-injected rewinds inside functions.
Returned output descriptors likewise do not represent all internal descriptor
traffic. Storage reservations and generated native stack bounds must remain
separate from measured whole-process RAM in subsequent performance comparisons.

Direct controller checks pass at O0, O2 and with trap-based undefined-behavior
instrumentation. The bank checks pass 44 cases at O0/O2, and the existing graph
controller checks pass at O0/O2. Their component logs are retained separately;
the listed workspace hashes were collected afterward, so they are not a claim
that all compilation inputs were frozen for those earlier runs. The integrated
verifier records before/after hashes and builds fresh artifacts in an isolated
output directory.

Before native execution, review of the first compiled module also exposed an
oracle assumption: a layout index is not always `typeId - 1`. The compiler
assigns stable type IDs but emits only reachable layout entries, ordered by
those IDs. This fixture uses Int and Bool but no Unit, so the omitted Unit
creates a gap. The acceptance fixture must derive the reachable layout order
from the source and that rule; adding an unused runtime type merely to satisfy
the original expectation would hide the harness defect.

The first native O0 run passed the Unicode/empty lifecycle byte checks and
reported the expected small-lifecycle copy totals, but failed four harness
predicates. Source review traced them to incorrect expectations: truncated
content fails after output invalidation rather than in structural preflight;
the token selected for a stale test was actually the most recently completed
duplicate; the scratch test expected `s` where its continuation input was `c`;
and the large-request descriptor total omitted a successful callback whose
subsequent bank publication failed. That total is five returned descriptors
(1 initialize + 2 begin + 1 failed publication + 1 retry), not four published
descriptors. The corrected stale scenario exercises a genuinely older token.
These post-execution corrections are disclosed; the final gate froze and reran
the corrected harness. No runtime change was justified by these four predicate
failures. A missing assignment in the test driver's storage-report snapshot was
also corrected before the final JSON substantiated its storage totals.

Fresh run `01750af91dfa4d668403d94eb3b6e10e` subsequently passed all 122 native
assertions at each of O0 and O2 with stable before/after input hashes. Its overall
gate still failed two of 180 checks: the test executable imports Windows heap
functions through CRT startup, whereas the generated module does not. An
executable-wide absence-of-imports predicate does not establish the intended
per-turn allocation contract. The final audit distinguishes generated module
and controller code from test-harness allocation/startup; these results do not
claim process-wide zero allocation or measure process RAM.

## Published evidence and next decision

The [evidence archive](evidence/131-owning-native-mailboxes/131-owning-native-mailboxes.zip)
contains 202 payload files and its internal manifest: source snapshots, generated
IR/C, focused and regression results, component logs, and the disclosed failed
attempts. The [storage index](evidence/131-owning-native-mailboxes/storage-index.json)
records each payload's byte count and SHA-256. Independent archive verification
checked every payload and the manifest before publication.

Archive SHA-256: `78cdc0aba8b652796b9d92d0a76834f04716ca523664299de5c024f84e0a3d6e`.

Next, compare this return-at-suspension path with retaining the actual working
arena through suspension. The latter must avoid intermediate State/Continuation
publication and reimport, preserve pending values on failed resume, and report
occupied arena slots separately from configured memory. First establish equal
results and failure isolation at O0/O2; then use bounded real I/O and matched F#
workloads to measure throughput, tail latency and whole-process memory. This
milestone selects no memory-policy winner and establishes no agent-reliability
advantage.
