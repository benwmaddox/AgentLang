# 138 — Native scratch reset profile

Status: optional profile implemented; local component, policy and real-I/O
validation pass. Starting revision: `cdf6669`.

This step removes avoidable full-capacity scratch writes from an opt-in native
mailbox build while preserving the diagnostic default. It follows the bounded
reset plan in [the async evaluation](../docs/ASYNC-ARENA-EVALUATION.md). It is
separate from agent efficacy: [report 137](137-guided-discovery-composition.md)
supports guided composition feasibility, without comparative reliability proof.

Acceptance requires unchanged typed state, errors, cancellation, failed-resume
retry, admission and ownership behavior for RETURN and KEEP at O0 and O2.
Existing frozen semantic fixtures remain authoritative. Focused checks must
distinguish actual reset writes from cursor extent, demonstrate the optional
profile avoids capacity-sized payload clearing, and retain rejection of
uninitialized reads after reuse. The semantic context ABI and compiler rewind
decisions remain unchanged.

The optional build flag is `-DAL_MAILBOX_FAST_RESET=1`. It skips only the two
full-capacity owning-scratch payload poison fills at checkout and release.
Initialization fills, live-prefix poisoning, initialization/poison bitmap
tracking, retained-bank handling and compiler rewinds remain unchanged. An
additive 48-byte reset-stats getter reports saturating counters; existing public
ABI structures are unchanged. Three private counters add 24 bytes to the
controller, included in its reported storage requirement.

## Observed reset work

The policy verifier passes 479 checks across four builds/runs (diagnostic and
optional profile at O0/O2); each native policy runner covers RETURN and KEEP.
All 21 source hashes remain stable. The following figures are totals for that
bounded fixture, identical at O0 and O2:

| Checkout/release work | Diagnostic | Optional profile |
| --- | ---: | ---: |
| Full-capacity payload bytes requested | 3,538,944 | 0 |
| Live-prefix payload bytes requested | 2,032 | 2,032 |
| Bitmap one-byte store operations | 888,800 | 888,800 |
| Cursor extent reclaimed (separate metric) | 2,032 | 2,032 |

These count logical runtime-issued/requested stores, not measured hardware
traffic. They exclude initial scratch, retained-bank and staging fills, and
exclude ordinary within-handler allocation/rewind activity. The existing
`turn_reset_bytes` counter remains cursor extent, not a total-write measure.

The native component test writes a sentinel beyond the live cursor, returns and
reuses the arena, and observes the sentinel surviving only in the optional
profile. It then checks that initialization tracking rejects reading that byte.
It also checks exact counter formulas and getter alignment, overlap and
wrong-thread rejection. This tests actual backing bytes in addition to telemetry.

## Validation history

An initial native compile caught a miscounted size assertion for the additive
stats structure (40 instead of 48 bytes); no binary was produced from that
attempt. A subsequent compiler attempt encountered temporary-object rename
errors; fresh sequential runs used a workspace TEMP/TMP directory.

The first integrated socket attempt completed all eight native runs but failed
the final source-integrity check because header/verifier edits overlapped the
run. It is not accepted. The next frozen-source run passes 139,557 verifier
checks, with eight native processes and 85 stable source hashes. Those checks
include repeated fragmented-I/O fields; they are not independent scenarios.
Seven cases per run cover Unicode/NUL, empty text, replacement, cancellation,
retry, interleaved mailboxes and 4 KiB request/completion data. A final source
cleanup removes unused verifier helpers before the publication rerun. That
final run (`4cbaaf69d8cc46b795d63032ecfb756d`) passes 139,765 checks, all eight
native processes and all 85 source hashes. Fragmentation changes the number
of repeated field checks between runs; the seven acceptance scenarios are
unchanged. Provider totals are 96 accepted connections, 88 completed responses
and eight I/O failures in the intentional cancellation windows, with no other
error category.

Across the socket fixture, the optional profile eliminates 15,204,352 requested
full-capacity payload bytes under RETURN and 9,437,184 under KEEP at either
optimization level. RETURN still performs 69,536 live-prefix payload writes
and 3,940,160 bitmap stores; KEEP still performs 60,624 and 2,480,544 respectively.
Existing state, status and runtime counters agree across profiles. These totals
cannot select a throughput winner between RETURN and KEEP.

All nine final standalone component build/run combinations pass: owning tests
at O0/O2 in both profiles, existing non-owning mailbox tests at O0/O2 diagnostic
and O2 optional, and owning tests under trap-based undefined-behavior checking
in both profiles. An earlier component-runner command omitted owning runtime
dependencies and failed at linking; its logs remain separate from the corrected
run. Early size-assertion/temp failures are summarized in the component evidence,
not reconstructed as original raw logs.

The additive getter's size/offset assertions compile, existing ABI assertions
remain unchanged, and reported controller storage includes the added private
counters. A read-only review found two driver paths that could omit failed
telemetry reads; both now fail validation. No language syntax, semantic IR,
compiler rewind logic or frozen expected behavior changed. Full .NET Release
and LLVM suites were not rerun for this native-controller/verifier change.

Reproduce the integrated checks locally on Windows:

```powershell
pwsh -NoProfile -File scripts/Verify-OwningMailboxPolicy.ps1
pwsh -NoProfile -File scripts/Verify-OwningMailboxRealIo.ps1
```

The [evidence index](evidence/138-native-reset-profile/storage-index.json)
locates final source snapshots, exact component compiler arguments, fresh logs,
generated IR and the failed/intermediate/final verifier runs. Binaries and build
caches are excluded. The intermediate passing verifier script is preserved;
the initial source-integrity-failed run does not have a reconstructed source
snapshot. Archive payloads and source immutability are hash-checked.

This is an intermediate profile, not a production throughput claim. Live-prefix
poisoning, initialization tracking and diagnostic snapshots remain. Sustained load,
whole-process memory, acceptable tail latency and a matched F# application are
still needed before choosing a mailbox memory policy or claiming a performance
advantage.
