# 139 — Trusted generated native profile

Status: implemented and locally validated. Starting revision: `533980f`.

The next step toward a representative native benchmark is one explicit release
profile for trusted generated code. Report 138 removes two bulk scratch fills,
but its remaining per-byte tracking still represents a diagnostic runtime.
This change removes that tracking as one explicit profile. The diagnostic host
remains available for correctness investigation.

## Contract

The diagnostic profile remains the default. The opt-in C build uses
`AL_OWNING_TRUSTED_GENERATED=1`; the mailbox compiler receives an explicit
typed profile option and records it in the manifest fingerprint. Generated
metadata and runtime objects must use the same option as the host.

Context and module structure layouts remain unchanged. The profiles have
disjoint bitmap shapes: trusted contexts have null initialization/poison
pointers and zero bitmap bytes; diagnostic contexts have valid mapped bitmaps.
Runtime begin, generated preflights, bank validation and mailbox storage checks
must enforce that distinction. Opposite-profile contexts must be rejected before
dispatch or output publication.

Trusted code is responsible for initialized values, valid retained roots and
compiler-proven lifetimes. The normal mailbox path supplies retained slices
from its own attachment roots. Direct native callers control raw context memory
and must supply valid initialized roots; this is not isolation from a hostile
native library. Diagnostic bitmap checks remain available to detect compiler
and host mistakes. Removing them must not be described as preserving rejection
of every deliberately corrupted internal context.

External serialized values still require full bounds, layout, type-index,
string-header, UTF-16/padding and overlap validation. Capacity, cursor, call/step,
arithmetic, thread, token, cancellation and transactional publication checks
remain active. Actual value construction and padding writes remain; only
diagnostic poison, per-byte tracking and related diagnostic scans are removed.

## Acceptance

- No bitmap allocation or per-byte tracking/poison in the trusted runtime path.
- Unchanged generated-language outcomes for ordinary and associated mailbox
  execution at O0/O2, including retry and terminal cancellation.
- Diagnostic malformed-internal-context tests remain diagnostic tests; external
  malformed-input and capacity guards are checked in both profiles.
- Opposite-profile host/module combinations reject without partial publication.
- Report actual storage differences and remaining runtime costs. A matched F#
  sustained-load comparison is still required before claiming throughput gains.

## Validation

The focused diagnostic owning-runtime tests pass at O0/O2 (507 checks and 43
cases per run). Trusted runtime tests pass at O0/O2 and with UBSan trap mode.
Trusted mailbox host tests pass at O0/O2, covering dirty caller storage,
cancellation and slot reuse, malformed UTF-8, capacity-failure preservation,
and rejection of the opposite bitmap shape. Component summaries preserve
the successful exits, post-validation source hashes, and earlier failed build
and command attempts under `.agentlang/trusted-release-139/{core,host}`.

The fresh native owning-value-stack verifier passes 35 checks, including 33
compiler lifetime assertions and the same-IR backend comparison. Its source
hash gate passed. Evidence is
`.agentlang/owning-stack-003/verification-2fb7f1e176f3469c8a45d3df9abbe6d4/verification-evidence.json`.
The installed Windows UBSan runtime did not link in normal reporting mode;
the separately built UBSan trap-mode test passed. The verifier retains both
results rather than treating the unavailable reporting runtime as a pass.
The freshly built full LLVM suite passes 476 assertions; its log and exit code
are under `.agentlang/trusted-release-139/llvm-regression`, with generated
artifacts under `.agentlang/native-validation/tests-66374be417c74af1b2388f3d2d768c5b`.
The final ordinary mailbox matrix passes 588 checks across four module builds and
six native runs, with both directions of host/module profile mismatch rejected
before normal or associated callback publication. All 22 recorded source inputs
remained unchanged. The focused fixture's caller-reserved storage falls from
165,536 to 149,152 bytes: exactly 16,384 bytes of removed scratch bitmaps, with
retained banks, staging and controller metadata unchanged. This is reserved
controller storage, not whole-process memory. Evidence is
`.agentlang/owning-mailbox-001/integration-run-f9b54db921d448f689a272b695594463/integration-evidence.json`.

The first ordinary run failed a driver assertion that still assumed diagnostic
bitmaps. Its evidence remains in `integration-run-738e7033f2a944ec8c524b84f931f55e`;
the profile-aware arithmetic was corrected before the intermediate 586-check
passing run `integration-run-b748b8d546d0482ead6b8e0afb590ad4`.
The first policy run then found another driver assertion with a hardcoded
diagnostic scratch total. Review also found that the first
associated mismatch fixture used malformed retained strings, which could reject
for the wrong reason. The final fixture uses valid empty roots and a same-profile
success control. Its first positive-control assertion incorrectly demanded an
output packed directly after the roots. Instrumentation showed successful
execution with the correct empty value at offset 72 and cursor 80: intermediate
allocations remain in the arena as intended. The corrected control validates
the returned location and bounds instead of requiring compaction. That failed
attempt is `integration-run-4eeccb46d3924433bdf53ad06578137b`. The final ordinary
run passes both same-profile controls and both mismatch directions. A focused
run additionally checks the controls against all four frozen DLLs, with six
successful executions and stable source hashes in
`.agentlang/owning-mailbox-001/profile-focused-595bf02669804f058f08539946452ed9`.

The final policy matrix passes 741 checks across four module builds and six
native runs. RETURN/KEEP behavior, admission outcomes, publication/copy counts,
lease accounting and reset cursor counts agree across profiles; storage and
diagnostic reset counters follow the independent profile-specific expectations.
Source hashes remain unchanged. Evidence is
`.agentlang/owning-mailbox-002/policy-run-ae341186006a48dd9c92fec74e22743c/policy-evidence.json`.
The earlier failed `policy-run-f6d6cf3c299546838a696e0d089aa36b` and intermediate
passing `policy-run-ab9072b0f37e40b985289b34833f81b1` and
`policy-run-c1480f7cd5b540e0b5f0644c2baf8be0` remain separate attempts.

The real-I/O matrix passes 204,422 checks with no failed checks: all seven frozen
cases run under O0/O2, RETURN/KEEP, and diagnostic/fast/trusted-generated profiles.
All 85 source hashes remain unchanged. The local provider recorded 144 accepted
connections, 132 completed responses and 12 expected cancellation I/O failures;
other error counters are zero. Evidence is
`.agentlang/owning-mailbox-003/io-run-bd7b743b7e1541fdbc48f536ef119fc6/io-evidence.json`.

In that fixture, total caller-reserved storage falls from 935,784 to 804,712
bytes. The 131,072-byte saving is exactly the two removed bitmaps for each of two
262,144-byte scratch slots. Both policies retain equal configured storage.

| Checkout/release metric per seven-case run | Diagnostic RETURN | Diagnostic KEEP | Trusted RETURN | Trusted KEEP |
| --- | ---: | ---: | ---: | ---: |
| Full-capacity payload writes requested, bytes | 15,204,352 | 9,437,184 | 0 | 0 |
| Live-prefix payload writes requested, bytes | 69,536 | 60,624 | 0 | 0 |
| Bitmap store operations | 3,940,160 | 2,480,544 | 0 | 0 |
| Cursor extent reset, bytes | 69,536 | 60,624 | 69,536 | 60,624 |

O0 and O2 agree. The intermediate fast profile removes only the full-capacity
writes. These counters describe requested runtime operations at checkout/release,
not measured hardware memory traffic or all program writes. Value construction,
serialized input validation, publication copies, admission and token bookkeeping,
and scalar accounting remain. No automatic compaction was introduced.

## Runtime organization

Specialized typed mailboxes connected as a data-flow system remain an open
candidate, with functions providing ordinary composition and mailboxes defining
state, scheduling and lifetime boundaries. Live inspection and implementation
replacement are useful Smalltalk-inspired directions. This profile work does
not commit the language to general actor spawning, supervision or distribution.

No throughput result is claimed yet. Agent efficacy remains separate: reports 125,
135 and 137 demonstrate bounded feasibility and reuse, without establishing a
general reliability advantage over F#.

## Reproduction and next step

Run locally on Windows with the configured LLVM toolchain:

```powershell
pwsh -NoProfile -File scripts/Verify-NativeValueStack.ps1
pwsh -NoProfile -File scripts/Verify-OwningMailbox.ps1 -SerialBuild
pwsh -NoProfile -File scripts/Verify-OwningMailboxPolicy.ps1
pwsh -NoProfile -File scripts/Verify-OwningMailboxRealIo.ps1
```

The full LLVM test executable was freshly built by the owning-stack verifier
and then run without a suite filter. The repository-wide Release gate was not
rerun; validation targeted the changed compiler/native boundary and its callers.
The [evidence index](evidence/139-trusted-generated-native-profile/storage-index.json)
locates component exits, failed and intermediate attempts, final integration
results, generated source and final source snapshots. Binaries and build caches
are excluded. Historical source is not reconstructed from failed-run hashes.

Next use this trusted profile for a small sustained-load comparison against a
matched F# workload, measuring successful completions at a common memory limit
and tail-latency target. Keep both arena lifetime policies in the comparison.
No additional actor framework is required to run it.
