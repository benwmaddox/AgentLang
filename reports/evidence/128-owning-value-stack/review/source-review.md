# Owning stack architecture and runtime review

Status: in progress, 2026-10-08. This review observes the separate experimental backend only. Source review was against `native/owning_stack_runtime.[ch]` while `OwningStackAot.fs` was still being written; recheck the source before closing each item.

Later backend review note: the runtime changed `al_owning_update_live` so `LivePayloadBytes` is operand-only and `LiveLocalPayloadBytes` is local-only. Adding them for final combined owned bytes is correct; an earlier verbal concern about double-counting those two fields is withdrawn. The native storage test's separate duplicate double-count finding remains independent.

## Required ownership cases

- `Scope` must preserve outer owned locals even when an inner store reuses a local slot. The interpreter discards the inner locals map; the verifier permits stores that replace a slot with another type. Per-environment local storage needs the largest reachable slot size and proper nested shadow locations.
- A callee returning more bytes than its arguments consume must reserve caller result space before opening its frame. Test zero-input `Envelope` return and multi-output return over an older live stack prefix; poison/reuse callee bytes and decode afterward.
- All consuming operations reclaim the full top extent. Field access copies the selected nested field before poisoning the record. `dup` creates disjoint bytes; dropping either copy leaves the other readable. Empty records need distinct logical stack entries despite zero payload bytes.
- Recursive calls need bounded dynamic frames, depth and step guards. Unsupported verified operations/types must fail closed at compile. `StoreLocal` owns bytes; a Flow `drop(local)` drops a fresh `LoadLocal` copy and must not be reported as releasing the local owner.

## Runtime findings to address

1. `al_owning_bit_bytes` adds 7 to a `uint32_t` capacity and can wrap. Reject oversized capacities or compute `n/8 + (n%8 != 0)`; check host allocation sizes and offsets too.
2. `al_owning_move_range` bounds source/destination by capacity but not current cursor. A destination can be marked initialized beyond the live region. Enforce both ends within `cursor_bytes` for live values.
3. `al_owning_local_reserve` adds unchecked `uint32_t` values. Live-byte updates cast payload counts to `int32_t`, which can wrap for large requests. Bound arithmetic or widen checked deltas.
4. `al_owning_store_local` zero-fills its destination before copying the source. Enforce nonoverlap of owner/local and operand regions, or stage before clearing.
5. `al_owning_bytes_copy` orders unrelated pointers, which is undefined in C. Use `memmove` and count actual copied bytes.
6. `poison_reuse_checks` inspects an initialized byte that still has a poison flag, while most writers clear that flag. Track poisoned-region reuse at initialization or make the metric's narrower meaning explicit; all decoding must check full initialization.
7. Failed status currently causes release and local-counter routines to return early. Failure epilogues must rewind and poison owned bytes and restore live counters while preserving the first error/status.
8. Two bitmaps and the trace buffer are host allocations outside the language stack. Report their reserved bytes and trace truncation; never infer complete event counts from a truncated trace.
9. `al_owning_drop` uses a historical duplicate event's original source offset to assert survivor validity. Dropping or moving the original first, or swapping values, invalidates that address while the duplicate remains a valid independent value. The runtime must not reject those legal sequences; use fixture-specific survivor assertions or track owner identity through moves.
10. Copy and move counters generally add logical `payload_bytes`, while the runtime copies the full `extent_bytes`. Unit and empty records have an eight-byte token extent and zero payload. Report physical bytes moved separately from logical payload bytes, and pin the oracle to the metric being used.
11. Initial `owning_stack_runtime_test.c` double-counted the duplicate: `al_owning_duplicate` already increments live payload, then the test manually added it again and subtracted the inflated total at teardown. Assert live 32 after duplication, 16 after dropping its copy, and zero after final release. Simulated return moves do not replace an integration test of generated call cleanup.
12. F# layout and encode/decode totals used unchecked `int` addition (`offset + fieldPayload`, `List.sumBy` of extents). Acyclic nested records can expand beyond the signed/unsigned native offset range; reject overflow before allocation or LLVM emission.
13. Initial `ExecuteIntoCore` passed a pinned caller retained-output array directly to native. Atomic publication needs staging and host copy only after all native validation/cleanup and decode succeed, or a proof that every possible failure precedes the sole infallible write. Account for staging reservation and host copy separately.

## Emitter review (current source while implementation is active)

- Root local flags were initially absent, so `makeBindings` failed for any local. The implementation agent added flags to all root slots; recheck with a compiled local fixture.
- `If` initially emitted unused empty `if.then.end` and `if.else.end` labels with no terminators. The implementation agent removed them; run a branch fixture through O0/O2 native compilation.
- Non-interpolated F# `Line/Inst` calls still contain `%%ctx`, `%%input`, and `%%entry.return.status`. Those produce literal doubled percent identifiers; use single `%` in plain string literals. Interpolated strings use `%%` as their escape.
- C live accounting now separates operand and local payload. Current `FinalLiveStackBytes` includes operands only; a combined final-owner metric needs both or a distinct local-final field. Do not mix this with the earlier runtime variant that counted locals in global live.
- `emitOffset` adds dynamic frame/cursor bases and static relative sizes in i32. Static checked sums do not prevent a dynamic result above Int32.MaxValue. Guard widened dynamic sums before reserve/access and keep required/available capacity values representable and nonnegative in the host exception.
- Direct retained-output writes were replaced with bounded host staging followed by decode and commit; verify the final emitted publication and host metrics together.
- Current source now fixes root flags, empty `If` labels, plain-literal `%` operands, combined final live accounting, and the nested empty-field in-parent extent. The dynamic i32 offset/capacity issue remains under review while the comparison verifier runs.

## Final offset audit in progress

At SHA256 `7077DE3F2C82A0D4E208B3ED25D59DB8C27411E03C8AD7E1090E6A89EBEE9465`, `emitOffset` remains `add i32`. Static layout/operand sums are bounded by Int32.MaxValue. A successful dynamic base is a live cursor/offset bounded by declared stack capacity, also at most Int32.MaxValue; one addition therefore cannot wrap uint32. A frame `operandBase` is computed before its `frameEnd` reserve, but no memory operation uses it if that reserve fails. The frame-end sum itself is below UInt32.MaxValue and C rejects it against capacity; int64 host exception fields now represent a required count above Int32.MaxValue without a negative cast. This path argument should be rechecked after any offset-guard change. A widened guard can make the invariant local to each addition, but is not evidence of a current unsigned wrap by itself.

The native entry boundary also enforces the premise. `al_owning_begin` rejects a context with `stack_capacity_bytes > INT32_MAX` as INVALID_REQUEST after zeroing cursor/live counters. The generated `agentlang_owning_execute` calls begin and branches on nonzero status before importing input, reserving storage, or invoking any frame emitter. An external native caller cannot bypass the signed-capacity bound by calling the exported entry with a larger uint32 capacity; managed `Execute(int)` is not the only guard.

The one highest-value missing semantic fixture is an outer live record local shadowed by a different-size inner record in Scope, followed by a read of the outer after scope exit. That directly exercises the planner's owner restoration and frame-local accounting at O0/O2; the current runner already covers return, branch, copy/drop, capacity, and retained publication separately.

## Narrow live-byte accounting audit

`IrUnit` currently has `PayloadBytes=8`, but Unit constant emission calls `al_owning_store_token` without adding 8 operand-live bytes. Later `drop`, `StoreLocal`, or retained-output cleanup subtracts/depends on 8 and can underflow. Add the +8 after Unit token creation, or consistently define Unit payload as zero throughout layout, record fields, copies, and cleanup. Int/Bool constant +8 updates are present. Empty records are consistently zero logical payload with an eight-byte stack token; `MakeRecord` preserves the sum of field payloads, and `GetRecordField` adjusts parent-to-field payload. C local load/store/clear updates the separate operand/local counters; call input and result moves transfer ownership without changing total live payload, while callee operations account for any input/output size difference.

## Minimum adverse validation

- Capacity one byte below a nested return's required peak, including a zero-argument callee that returns a larger record; prior retained state remains unchanged and final cursor/live bytes return to baseline.
- Overwrite a local with a larger different type, then exit a nested scope that shadowed an outer local; decode the outer value.
- Duplicate an `Envelope`, drop each in turn, extract a nested field, and repeat under fixed capacity; exact cursor baseline after each sequence.
- Duplicate, swap, and drop in both orders; move the original into a local before dropping the duplicate.
- Recursive call reaches depth/stack limit with preserved first diagnostic and no owned-byte leak.
- Empty record/Unit and zero-byte payload accounting; swap unequal extents if supported.
- Inputs and retained destination near declared capacities, failed preflight, and any permitted alias pattern. Report stack, bitmap, trace, retained, and host reservation separately.
