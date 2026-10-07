# Typed state re-entry across native invocations

Status: bounded typed state re-entry validated, 2026-10-07.

## Objective

Report 106 established retained native outputs. The next prerequisite for mailbox
turns is feeding that state into a later typed invocation without decoding and
re-encoding its record graph through managed values. Verified IR already declares
body input types; before this milestone both backends rejected nonempty entry stacks.
This milestone enables typed re-entry. Same-mailbox suspension follows afterward.
It does not add a scheduler, source syntax, real I/O or a server benchmark.

## Contract

Initialization and subsequent bodies use the same VerifiedIrProgram object.
Opaque retained results keep that provenance and their frozen type metadata alive.
Reference identity deliberately rejects separately constructed snapshots even if
they have equivalent schemas. Cross-snapshot migration and standalone loading are
not part of this boundary. A newly compiled dictionary snapshot cannot automatically
consume state from an older snapshot; safe hot-replacement migration remains a
separate requirement. Existing empty-input APIs remain compatible.

An invocation accepts one retained input owner, selected roots and primitive
Int/Bool/Unit message arguments. Root selection can repeat an index while retaining
sharing. Validate count, exact types, provenance and disposal before invocation;
primitive messages cannot impersonate nominal values. No constructor/refinement
validator is rerun merely because a previously validated value crosses a turn.
The interpreter counterpart retains opaque runtime values under the same program
identity and initializes its block stack without a public-Value round trip.

The native bridge copies the entire input owner's immutable graph into empty
scratch using a C helper. Validate generation, directory, types, scalar encodings,
packed payload and older-child DAG before copying. Preflight exact byte/node
capacity. Preserve indices and rewrite record generations to the new scratch
owner. Input bytes and metadata must never be marked or mutated. Existing scratch
helpers then execute the body; existing output promotion exports live roots.
Each turn therefore copies the input owner and its live outputs. This is explicit
copying with measurable cost, not zero-copy persistent state. Unselected nodes
may be imported but are omitted by output promotion unless still reachable.

Borrow the input result under its result lock throughout native execution so
concurrent decode/disposal cannot free or mutate it. Failed import/handler calls
leave the input owner and public output roots unchanged. Scratch resets on every
exit. A future mailbox retains its prior state until replacement succeeds.

Version the expanded ABI as v3 with explicit readonly input owner/root/count
fields; do not repurpose the output retained descriptor. Preserve early version
rejection without reading the expanded tail, and validate readonly input spans
against writable backing. Historical ABI fixtures remain intact.

## Frozen host API surface

The shared qualified `IrEntryArgument` union contains `RetainedRoot of int`,
`IntArgument of int64`, `BoolArgument of bool`, and `UnitArgument`.
`IrInterpreter.executeBodyWithInputs host name body inputOwnerOpt arguments`
returns an opaque `IrInterpreterResult` with `Decode()` and `IDisposable`.
`NativeCompiledProgram.ExecuteRetainedWithInputs(name, inputOwner, arguments,
?options)` returns the existing opaque native retained result. Existing empty
entry methods keep their compatibility behavior. Input failures use coordinated
structured diagnostics; the same program identity is retained without keeping
the compiled DLL loaded.

## Frozen ABI v3

The existing 64-byte context prefix is followed by readonly input-owner pointer
at 64, ordered input-slot pointer at 72, input-type-ID pointer at 80, input count
at 88 and reserved u32 at 92. Size is 96 bytes with alignment 8. These arrays
contain resolved invocation arguments, including selected retained handles and
primitive message bits; they are not necessarily the owner's full root table.

`al_runtime_import_state(ctx, program, expected_input_type_ids, input_count)`
validates exact argument types against compiled inputs, imports the whole owner
if present, and writes rewritten arguments to workspace slots in the same order.
Workspace capacity includes input count. A null owner supports primitive-only
and empty calls. Import runs after ABI validation, even for empty input.
It returns existing helper result enums; scratch-capacity diagnostics report
required total payload bytes and nodes. No import failure mutates input, scratch,
workspace or public output roots; complete validation precedes copying.

## Implementation ownership

- Core: opaque interpreter results and typed entry arguments, compatibility wrapper,
  focused input/provenance/diagnostic tests. No new semantic IR operations.
- Native runtime: ABI v3 input validation and immutable graph import, C harness.
- LLVM backend: typed body entry arguments, provenance/borrow/ownership integration,
  fresh runtime compilation and import resource diagnostics.
- Integration tests: interpreter/native independent parity, lifecycle, capacities,
  raw ABI fixtures and canaries. Reports/evidence stay with the coordinator.

## Acceptance

- Initialize nested/refined state, execute several typed turns and compare against
  independent expected values and the interpreter at O0/O2.
- Keep state opaque between turns; decode only final observations after prior
  owners, scratch and producer DLLs have been disposed.
- Preserve repeated-root and nested sharing; do not rerun validators on import.
- Reject different program objects, wrong types/counts, disposed owners and bad
  handles before execution. Keep old state unchanged on errors/capacity failures.
- Exact-fit and one-short native import byte/node budgets, input byte identity,
  public-output canaries and coordinated disposal/borrow safety.
- Match instruction counts, value limits and complete language diagnostics.
- Audit ABI versions/offsets, source snapshots and frozen experiment binaries.

Run the exact local native conformance script, direct C harness and focused
IR/interpreter suites with fresh isolated build outputs. Preserve failed attempts
and final artifacts in evidence. Publish only with reports updated. Comparative
agent-reliability superiority remains unproven; this slice advances the secondary
execution target rather than supplying a new actor comparison.

## Validation results

The focused interpreter suite passes 49 assertions. The standalone native C
harness passes 178 assertions at each of O0, O2 and O1 with UBSan trap
instrumentation. Strict C warnings, formatting and undefined-symbol checks pass.
The ABI v3 C layout agrees with all 18 independent context layout values;
historical ABI v1/v2 fixtures remain unchanged. All ten frozen Release research
runtime files retain their recorded hashes.

The first integrated native attempt stopped when the new test fixture called
`int.add`, which is not a registered primitive; the correct call is `add`.
The earlier native sections completed, but that attempt is not a passing gate.
Its log is retained as `native-conformance-01-failed.log`. The fixture was
corrected; its isolated state-entry section now passes at O0/O2. The repeated full gate
passes 441 assertions. A temporary reflection runner invokes the existing private test
method; assertion failures throw and propagate as a nonzero process exit.

The state tests also cover divide-by-zero after successful import followed by
successful reuse of the same opaque owner. Separate barrier-controlled tests
observe Decode and Dispose waiting on the existing WithBorrow lock. This tests
the synchronization contract; source review verifies that native execution stays
inside the borrow callback. It is not a timed native execution race test.

The complete native gate passes 441 assertions with a fresh isolated build and
zero warnings/errors. The full local Debug validation passes all 37 checks,
including 98 business-policy preflight checks across 30 independent expected
outcomes. Debug was selected to preserve frozen Release research binaries.
The full regression includes 114 IR and 49 interpreter assertions.

All 109 generated native DLLs have no PE imports or CLR headers, and every
extracted runtime source matches the checked-in C implementation. Source and
emitted IR archives, artifact hashes, exact commands, the failed first attempt,
and successful final logs are in
[evidence/107-native-state-reentry](evidence/107-native-state-reentry/index.json).
These results cover the working tree based on `ba336e3`; its source hashes identify
the tested files rather than implying the base commit contains this milestone.

Commands:

```powershell
pwsh -NoProfile -File scripts/Validate.ps1 -Configuration Debug -ReportPath .agentlang/state-reentry/full-debug-validation.json
pwsh -NoProfile -File scripts/Verify-NativeConformance.ps1
```

The direct C reproduction script is archived under `runtime/direct/`. The
barrier tests require only test-assembly friend visibility, not a public runtime
hook or an arbitrary .NET language escape hatch.

Review corrected the second-turn capacity oracle before execution: initial
state includes an unselected Orphan node (56 bytes / 4 nodes). Turn one imports
all of it, then allocates a 24-byte Envelope (80 bytes / 5 scratch nodes), but
retains only the live 48-byte / 3-node graph. Turn two imports that smaller graph
and needs 72 bytes / 4 scratch nodes. The tests must use these distinct exact
budgets rather than carrying an oversized first-turn allocation forward.

## Limits and next decision

This is a typed native invocation boundary, not an implemented mailbox system.
The next bounded demonstration is a single mailbox holding opaque state across
a suspend/resume boundary, returning scratch storage while suspended and swapping
retained state only after a successful turn. A pending I/O token can initially be
deterministic; real I/O, cancellation, bounded queues and a saturated-server
comparison still require separate implementation and evidence.

The current boundary copies the complete input owner and promotes live outputs.
It does not establish throughput, a final memory design, a compiler-free release
package, JIT invalidation or safe migration across dictionary snapshots. The
latest agent efficacy conclusions remain reports 101 and 102: tested discovery,
reuse and guided repair are feasible, but comparative reliability superiority
has not been demonstrated. This milestone supplies no new agent comparison.