# 129 — Variable-sized owning values

The owning-stack backend now executes variable-sized String values and nested
records at LLVM O0 and O2 with the same verified semantic IR as the interpreter.
The fresh focused run passes 796 comparison checks and 29 verifier checks with
stable source hashes. The full local Release gate passes all 37 checks. The
[final evidence index](evidence/129-variable-owning-values/owning-stack-002-final/index.json)
links exact source snapshots, generated IR, results and validation logs.

This establishes a bounded correctness result. It does not establish better
throughput, lower process memory, F# structural parity or stronger agent-editing
reliability. The current implementation performs substantial copying and packing.

## Delivered behavior

Strings are stored inline as an eight-byte header (u32 UTF-16 code-unit count,
u32 reserved zero), raw little-endian code units, and zero padding to eight-byte
alignment. Nested records use declaration-order fields and runtime offsets after
variable-sized data. Values own independent payloads; local loads copy, moves
invalidate their old source, and compiler-controlled cleanup reclaims storage.
No source-language lifetime or region annotations were added.

The interpreter/native comparison covers short and long strings, empty strings,
NULs, astral pairs, isolated high/low surrogates, and a surrogate pair formed by
concatenation. Independent literal byte oracles check the physical encoding.
Further cases cover nested Empty/String/Int records, dynamic branch results,
same-slot scope shadowing, larger multi-output returns, zero-output user calls,
dup/drop, native errors and capacity failures with unchanged caller output.
Success and failure paths check zero final cursor and live operands.

Layout metadata distinguishes exact sizes from minima and dynamic offsets.
A 64-node descriptor path (63 records plus its Int leaf) is accepted; 65 nodes
are rejected with `IR_OWNING_STACK_LAYOUT_DEPTH`, including a cached-child-order
control. Raw C validation also bounds descriptor counts and cyclic/malformed
layouts. The layout scanner uses an explicit 8,192-byte memo table.

Fixed-record controls still compare interpreter, the older shared-graph native
backend, and owning native execution. The older backend does not support these
String types, so String results are an interpreter/owning comparison, not a
three-backend result. This experiment round-trips through host input/output
marshalling; it is not yet a persistent native owning-mailbox server.

## Memory and movement observations

The following byte counts match at O0 and O2 for the nested String turn:

| Input case | Input extent | Output extent | Peak working extent | Deep-copy bytes | Moved bytes |
| --- | ---: | ---: | ---: | ---: | ---: |
| Empty | 32 | 48 | 416 | 640 | 2,336 |
| Short ASCII | 40 | 48 | 488 | 712 | 2,512 |
| Long BMP/astral | 48 | 80 | 752 | 1,168 | 4,416 |

These are instrumented execution counters, not whole-process memory. The main
cases have a 4,096-byte configured working capacity and 328,872 bytes of trace,
bitmap and context storage: 327,680 trace bytes, 1,024 bitmap bytes and a
168-byte runtime context. Generated frame metadata is reported
separately (372 bytes per-frame maximum; 24,576-byte configured peak bound), as
is the scanner memo table. These are not measurements of total machine-stack
usage. Host input/output staging is also reported separately. Operand and local
peaks are not necessarily simultaneous and must not be added together.

A compiler-verified direct concat of two one-code-unit inputs succeeds with
exactly 80 bytes and fails safely at 79: 32 host-input bytes, 32 entry-frame
argument bytes and 16 scratch bytes. The existing wrapped user-function path
remains tested separately and peaks at 144 bytes. It adds a 32-byte user-call
argument frame and two 16-byte named-local loads. An earlier 112-byte estimate
missed those loads; emitted IR and the final trace establish the complete
schedule. Neither number is a language-level lower bound or an optimized-runtime
promise.

A mixed Empty/String/Int record with three String code units has 22 payload
bytes and a 24-byte extent. Padding is occupied storage, not logical payload.
The tests distinguish these quantities explicitly.

## Corrections and evidence quality

Integration exposed a runtime bug in leftward nonadjacent moves: source
invalidation also poisoned live bytes in the intervening gap. The fix invalidates
only old-source bytes outside the destination. New disjoint left/right and
overlap regressions check bytes, initialization/poison bits, counters and traces.
An independent temporary build restoring the old expression fails the new live-gap
assertion. [Archived raw evidence](evidence/129-variable-owning-values/storage-index.json)
contains the tested sources, passing outputs and negative-control evidence.

The final harness also corrects allocation-versus-construction event assumptions,
associates field offsets with the current typed record instance, and uses
independent literal offsets alongside layout formulas. Mixed-record metadata is
checked against its own compiled program; unused types are not expected in a
pruned entry program. Capacity probes distinguish direct and wrapped bodies.
Depth tests count terminal scalar descriptors. Diagnostic output summarizes deep
values and uses code-unit hex for isolated surrogates; it does not change the
existing public value-inspection depth or Unicode serialization limitations.

## Local validation

Final focused run:
`verification-d6b52498c0ca46a08ae5b309c28a6f40` under
`.agentlang/owning-stack-002/`.

| Validation | Result |
| --- | --- |
| `pwsh -NoProfile -File scripts/Verify-NativeValueStack.ps1` | 796 comparison checks; 29 verifier checks; stable inputs |
| Strict C storage O0, O2, UBSan-trap | 36 cases / 453 checks each |
| Freestanding runtime and module/runtime DLL links | O0/O2 pass; no `memset` dependency |
| Existing LLVM regression runner | 476 assertions pass after a fresh serial Release build |
| `scripts/Validate.ps1 -SerialBuild -SkipPackageAudit` | 37/37 checks pass, including 98 business-policy preflight checks |
| Formatting and whitespace | C clang-format check and Git diff checks pass |

The full Release gate ran from 11:28:39 to 11:58:34 on 2026-10-08. Final harness
refinements are covered by the later fresh focused build; the native implementation
remained frozen. The gate and focused runner use local temporary storage and
serial MSBuild. NuGet vulnerability auditing was explicitly skipped because its
service was unavailable. Normal Windows UBSan linking remains unavailable due
to missing sanitizer runtime symbols; the trap build runs successfully. No CI
run was used. This validates the working tree based on `eb68fa5`, not that base
commit alone.

After that frozen run, requirements and interpretation were updated for the
user's rare-movement and scope-rewind clarification. Of the 39 archived source
inputs, only `docs/STACK-ONLY-RESEARCH.md` differs from the final run; all compiler,
runtime, fixture and harness hashes still match. The archived document records
the contract tested then. The revised allocation direction is not implemented
or validated by these passing results.

## Interpretation and next step

Arenas are the general allocation model for language-managed dynamic values for
now; request/response applications are a particularly suitable first workload,
not their only intended use. Aim toward F# data/structural quality with C-family
syntax, better sustained throughput with a predeclared modest RAM increase over
F#, and mailbox isolation approaching selected Erlang/OTP properties. None of
those comparative outcomes is proved by this conformance slice.

After reviewing these results, the user clarified that data should rarely move:
the normal cleanup should rewind the bump pointer when the mailbox no longer
needs current data. The packed implementation does not meet that intent.
The final mixed-record trace moves the same 24-byte result six times during
local cleanup (offsets 112, 104, 88, 80, 72, 56, 48): 144 bytes moved, followed
by two return moves to offsets 24 and 0. These are measured physical relocations,
not allocation-pointer changes.

The next implementation must keep payloads in place across ordinary bindings,
read-only access and same-arena calls/returns, retain interior dead bytes and
bulk-reset only when safe. Removing per-local compaction alone is insufficient.
Preserve immutable value semantics and failure isolation without adding source
lifetime annotations. Compare live/occupied/dead extent, metadata traffic,
payload copying, capacity and execution time with this frozen baseline. This
report validates an experimental implementation, not the selected final policy.

The subsequent clarification requires compiler-proven early scope rewinds and
full request reset/pool return. Escaping results retain temporary bytes beneath
them; uncertain lifetime analysis omits the rewind. Runtime liveness checks are
not the decision mechanism. The planned replacement is specified in the
[stable arena lowering contract](../docs/STABLE-ARENA-LOWERING.md); this report's
tests do not validate that replacement.

The latest matched external-agent evidence remains
[report 125](125-matched-pair-repair.md): reuse works, but no reliability lead over
F# is established. Native owning-mailbox integration, real async I/O, matched
F# server performance and broader isolation guarantees remain outstanding.
