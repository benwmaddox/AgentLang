# Variable owning-value implementation plan

Status: interface freeze published 2026-10-08; schema-2 host layout/codec and
dynamic LLVM emitter compile. The first stable O0/O2 native String comparison
reached 509 checks with no native exception after the runtime's leftward-move
poison-range fix. The remaining five failures in that run were runner/fixture
expectations (four event selectors and one missing expected-extent field); the
comparison owner is correcting those before the final source-stable run.
The canonical checkout is `main`. This work extends the existing
`OwningStackAot` backend and leaves semantic IR, the ABI3 control backend, and
the fixed owning-value representation unchanged.

## Ownership and scope

- Backend ownership: `src/AgentLang.Llvm/OwningStackAot.fs` and, only if a
  shared F# codec/layout helper materially reduces duplication, one new F#
  helper included by `AgentLang.Llvm.fsproj`.
- Runtime ownership: `owning_stack_runtime.[ch]` and its direct C tests belong
  to the runtime worker. This plan freezes the descriptor and helper contract
  below; the backend will call those helpers and will not edit native files.
- Comparison ownership: fixture, runner, and script belong to the comparison
  worker. Root owns docs and reports. Preserve all unrelated checkout edits.

## Versioned layout metadata and native descriptors

The public experiment result adds `LayoutSchemaVersion = 2`. Its existing
`PayloadBytes`, `ExtentBytes`, and `OffsetBytes` fields keep their exact meaning
for fixed layouts. For a dynamic type or field size, the corresponding size is
`-1`, `IsDynamic` is true, and minimum payload/extent are separately reported.
For a field whose offset depends on an earlier dynamic field,
`OffsetBytes = -1` and `IsOffsetDynamic` is true. These sentinels are defined by
schema version 2; an observed instance size is reported only in execution
events. Fixed controls retain their exact schema values and sizes.

The immutable LLVM module layout uses native descriptor ABI version 1, separate
from the unchanged owning context/event ABI1:

```c
#define AL_OWNING_LAYOUT_ABI_VERSION 1u
#define AL_OWNING_LAYOUT_DYNAMIC_U32 UINT32_MAX
#define AL_OWNING_LAYOUT_MAX_DEPTH 64u
#define AL_OWNING_LAYOUT_MAX_TYPES 4096u
#define AL_OWNING_LAYOUT_MAX_FIELDS 65536u

enum al_owning_type_kind { I64 = 1, BOOL = 2, UNIT = 3, RECORD = 4, STRING = 5 };
enum { AL_OWNING_FIELD_ZERO_WIDTH = 1u };

typedef struct al_owning_type_descriptor {
  uint32_t kind, type_id, first_field, field_count;
  uint32_t fixed_payload_bytes, fixed_extent_bytes;
  uint32_t minimum_payload_bytes, minimum_extent_bytes;
} al_owning_type_descriptor;

typedef struct al_owning_field_descriptor {
  uint32_t child_type_index, fixed_offset_bytes, flags, reserved;
} al_owning_field_descriptor;

typedef struct al_owning_layout {
  uint32_t abi_version;
  const al_owning_type_descriptor *types; uint32_t type_count;
  const al_owning_field_descriptor *fields; uint32_t field_count;
} al_owning_layout;

typedef struct al_owning_value_size { uint32_t payload_bytes, extent_bytes; }
  al_owning_value_size;
typedef struct al_owning_field_location {
  uint32_t offset_bytes, payload_bytes, extent_bytes;
} al_owning_field_location;
```

`UINT32_MAX` marks only an unavailable fixed size/offset; minimum sizes remain
explicit. For a zero-payload empty-record child, `ZERO_WIDTH` makes its inline
parent extent zero while the standalone empty-record value remains an 8-byte
token. Descriptors are constant globals passed to helpers; no descriptor or
host pointer is stored in the ABI1 context. The C scanner rejects malformed
descriptor indices, inconsistent fixed offsets, overflow, owner-boundary
crossing, more than 4096 descriptors, more than 65536 fields, and nesting
beyond 64. F# rejects more than 4096 reachable value types
(`IR_OWNING_STACK_LAYOUT_TYPE_COUNT`), more than 65536 reachable fields
(`IR_OWNING_STACK_LAYOUT_FIELD_COUNT`), and verified type layouts deeper than
64 (`IR_OWNING_STACK_LAYOUT_DEPTH`) before compiling/entering native code.

Runtime worker will expose these checked operations (all return status and
preserve the first sticky error):

```c
int32_t al_owning_measure_value(ctx, layout, type_index, offset,
    containing_owner_end, error_id, out_value_size);
int32_t al_owning_measure_external_value(ctx, layout, type_index, source,
    source_length, source_offset, error_id, out_payload_bytes, out_extent_bytes);
int32_t al_owning_copy_external_bounded(ctx, destination_offset, source,
    source_length, source_offset, payload_bytes, extent_bytes, type_id, error_id);
int32_t al_owning_locate_field(ctx, layout, parent_type_index, parent_offset,
    parent_extent, field_index, error_id, out_field_location);
int32_t al_owning_string_length(ctx, offset, extent, error_id, out_code_units);
int32_t al_owning_string_concat_plan(ctx, left_offset, left_extent,
    right_offset, right_extent, error_id, out_code_units, out_payload, out_extent);
int32_t al_owning_string_concat_write(ctx, dst_offset, dst_extent,
    left_offset, left_extent, right_offset, right_extent, type_id, error_id);
```

The scanner validates the full initialized extent within the containing owner
and cursor. External malformed/truncated values produce
`INVALID_REQUEST` with the supplied error id; malformed live owners/descriptors
produce `INTERNAL`; checked physical exhaustion is reported by `reserve_to` as
`STACK_CAPACITY`; semantic concatenation overflow retains the verified String
diagnostic. String is exactly `u32 code-unit count`, `u32 zero reserved`,
little-endian UTF-16 code units, and zero padding to 8-byte extent. The scanner
validates canonical header, initialized data, padding, and bounds but accepts
every UTF-16 code-unit sequence, including isolated surrogates. `concat_plan`
is read-only; F# preflights the exact destination before mutation. `concat_write`
requires an already-reserved disjoint destination and prevalidates every range
before writing. It writes the complete canonical value and records physical
copies; F# changes live-owner accounting only after success.

External input uses the new bounded whole-extent copy helper after recursive
external scanning. The managed preflight supplies one independently measured
root extent per input in a bounded native `u32` size table. The native wrapper
passes that exact slice length to the scanner and verifies the returned extent
matches it, so one forged String count cannot absorb bytes from the next input.
It validates `source_offset/source_length`, copies and marks
the complete canonical extent (including internal String padding and later
record fields), charges logical payload to live ownership and copied extent to
input-copy metrics, and reports malformed source ranges as `INVALID_REQUEST`.
The existing fixed-only external-copy helper remains available to direct ABI1
storage tests.

The ABI1 context and 40-byte event shape stay unchanged. Event kind 14 is
`LOCAL_COMPACT`; actual dynamic offsets, extents, and payload bytes use the
existing event fields. The runtime worker may add String-source provenance
event kinds without changing the struct. Existing actual-size move, duplicate,
drop, and publish operations remain the physical ownership operations.
The runtime scanner uses a fixed 8,192-byte state/height workspace; expose this
as `RuntimeLayoutScannerScratchBytes`, separate from payload stack and generated
per-frame scalar metadata.

## F# lowering and memory invariants

1. Preserve the public `OwningStackAot.compile`, `Execute`, and `ExecuteInto`
   surface. Keep the existing closed verifier gate; add `IrString`/`LString`
   and only verified `string.concat` and `string.length`. Unsupported IR,
   targets, and effects remain rejected before native entry.
2. Extend type information with fixed-vs-dynamic size, minimum payload/extent,
   and each field's fixed-vs-dynamic offset. Keep Int/Bool/Unit/empty-record and
   nominal type IDs stable. The String root representation has payload
   `8 + 2*n`, extent `align8(payload)`; records sum child payloads/extents in
   declaration order, preserving the existing zero-width empty-record rule.
3. Encode input and decode output by recursively walking verified types. First
   measure with checked arithmetic, reject null/incompatible values and
   configured size/capacity excess before allocating host byte arrays, then
   encode exact .NET UTF-16 code units in little-endian order. Decode only after
   native success. Report native input staging, managed encoded bytes,
   retained staging, commit-copy bytes, and instrumentation separately.
4. Keep local and operand payloads tightly packed in the same bounded native
   stack. Per-frame scalar metadata tracks each active local's offset, actual
   extent, payload, and active state; it contains no values. The generated
   metadata bound counts every explicit alloca's type size and alignment
   padding in the largest emitted frame, multiplies that upper bound by the
   runtime's 66 raw frame entries, and adds wrapper alloca bytes. It is reported
   separately from the C scanner's fixed 8,192-byte workspace and payload stack
   bytes; it excludes LLVM spills, ABI call frames, and C recursion frames.
   Root/local and operand offsets are derived from the live packed layout, not
   a maximum-size String slab.
5. `StoreLocal` precomputes all source/target/suffix sizes and capacity. For a
   growing replacement, reserve the delta first. Relocate the complete
   contiguous suffix (later locals plus operands, including the source top
   when it lies there) overlap-safely; install the new owner; consume its old
   operand range; then publish new scalar offsets/extent/payload metadata.
   For shrink, install/relocate first and release the exact trailing bytes
   after all affected data is valid. Scope exit removes its owners and compacts
   later live data with the same preflight/commit rule. Metadata changes only
   after successful native moves. Any unexpected helper failure branches
   directly to baseline aggregate unwind and never consults stale offsets.
6. `LoadLocal`/`dup` make independent full-extent copies. `drop`, replacement,
   and scope exit consume/reclaim exact dynamic extents. Record construction
   and field extraction use the native scanner/locator, checked runtime field
   offsets, and complete inline copies. Concat reads two canonical input
   owners, plans exact output, reserves bounded scratch above them before
   consuming either, writes the full String, then compacts/transfers the result
   and reclaims both inputs plus scratch. Length consumes/reclaims the String
   and emits one 8-byte Int.
7. For a user call, derive actual argument extents and contiguous caller result
   extent. The callee takes ownership of copied arguments in its frame and
   returns all outputs contiguously in its frame. After local cleanup, measure
   the complete output range, then perform one overlap-safe whole-range move
   directly from the callee's top-of-stack result range to the caller's
   argument-start destination. The destination can overlap the source when
   outputs exceed argument bytes; the move helper validates all ranges before
   copying and handles that overlap. This handles zero outputs and multiple
   outputs without overwriting later results. Release to
   `destination + actual_output_extent`. On any failure, unwind to the captured
   baseline cursor/counters and never commit managed retained bytes.
8. Branch paths reconcile runtime layouts from the selected path; the verifier's
   static type join remains authoritative. Retained output is staged and
   decoded before a success-only copy into `ExecuteInto`'s caller-owned byte
   array, preserving its full contents on all failures.

## Files and validation

Backend changes are restricted to `OwningStackAot.fs` and optionally one new
F# helper plus its project include. Runtime C/H/tests, comparison fixture and
runner, and docs/reports have separate owners. Keep the existing 001 artifacts
and unrelated untracked files.

Validation is coordinated with the comparison owner and consists of a fresh
serial Release build (with `NuGetAudit=false` and repo-local `TEMP`/`TMP`),
focused O0/O2 owning execution of the exact verified String bodies against the
interpreter and independent literal byte/code-unit expectations, dynamic
layout/capacity/local-replacement/multi-output/zero-output checks, and the
existing fixed owning + ABI3 controls. The runtime owner separately runs C
storage O0/O2/UBSan-trap checks after freezing C/H. Preserve each fresh
verification run's source hashes and report unsupported or bounded cases
explicitly.

## Capacity-schedule audit

The `JoinStrings` candidate in the passing verification run
`verification-d6b52498c0ca46a08ae5b309c28a6f40` is a user-call body, not a
direct primitive body: the runner mints `mailbox.join-strings.entry` with
input types `[String; String]` and a single
`Call("mailbox.join-strings")`. Its generated O0 LLVM module is
`.agentlang/owning-stack-002/verification-d6b52498c0ca46a08ae5b309c28a6f40/native-output/owning-stack/strings/O0/join-strings/owning-stack-native.ll`,
SHA-256 `9EE7C0D77FFE41B75911246B615CDD56EEECA4017B639A3B73285926462644FC`.
Reading the wrapper, `@agentlang_entry_frame`, and `@agentlang_word_0000`
gives the following physical schedule for two one-code-unit input Strings:

| Step | Stack byte range | Cursor |
| --- | --- | ---: |
| Host input copy | `0..32` | 32 |
| Synthetic entry frame copies the 2 arguments | `32..64` | 64 |
| User-word frame copies the 2 arguments | `64..96` | 96 |
| First `LoadLocal` operand copy | `96..112` | 112 |
| Second `LoadLocal` operand copy | `112..128` | 128 |
| User-word concat stage | `128..144` | 144 |

The earlier 112-byte estimate omitted the two explicit operand copies emitted
for `LoadLocal`; the native metric reports `ReservedStackBytes=144`. The
capacity-80 run fails at the user-word frame copy (`RequiredBytes=96`) before
either `LoadLocal` or concatenation. The separately compiler-minted direct
body contains primitive `Call("string.concat")`, with no user-word call or
local loads: host inputs occupy `0..32`, the synthetic entry frame copies to
`32..64`, and concat stages its output at `64..80`. The direct-body boundary
checks pass at 80 bytes and fail at 79 bytes, with the exact required/available
boundary. Its generated O0 LLVM module in the same run is
`.agentlang/owning-stack-002/verification-d6b52498c0ca46a08ae5b309c28a6f40/native-output/owning-stack/strings/O0/direct-concat-capacity/owning-stack-native.ll`,
SHA-256 `0956E64D71A78B67C595E39BDE01008D081640EA773C942E8603B25F3249628D`.
The wrapped user-call body remains a distinct call-conformance case whose
observed high-water is 144 bytes. The cited final artifact reports 796 checks,
`failureCount=0`, and `sourceInputsStable=true`; its serial Release build and
O0/O2/UBSan-trap runtime validations completed in the same verification run.
