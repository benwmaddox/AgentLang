# Native dispatch implementation plan

Previous goal turn: progress. Report125 and its evidence were published at607d267.
Full PRD remains active. Canonical checkout prototype; preserve retention004 drafts.

## Objective and architecture
Move fixed-mailbox admission, pending completion identity, state owners and turn
execution out of the report108 .NET experiment host. Execute actual source-defined
Flow/2 handlers lowered through verified semantic IR. Use a native C standalone
process as the runtime test; .NET may compile fixtures and launch it, but cannot
schedule turns, own per-turn arenas, or supply managed invocation callbacks.

Preserve execution ABI v3 and all historical fixtures. Add module metadata ABI1
and separate mailbox-control ABI1. Related entries must originate from the exact
same backend-authorized VerifiedIrProgram instance. Compile indexed per-entry
LLVM objects (internal/private helpers stay local), one arena runtime object and
one immutable metadata object into a single DLL. Export agentlang_module_descriptor.
Keep existing single-entry compile behavior unchanged. Metadata includes version,
entry IDs/names, exact input/output type IDs, workspace capacities, diagnostics,
canonical type descriptors/names and a deterministic semantic/build fingerprint.
Fingerprint/type metadata must agree across fresh builds; optimization is recorded
separately. DLL lifetime must cover all native runtime/owner use.

Native controller uses caller-supplied bounded storage: fixed mailbox identities,
one shared scratch arena/workspace, two retained banks and root arrays per mailbox.
No allocation occurs during turns. Report both reserved backing (including both
banks) and live state; this is not a final allocation-policy selection. Native API
computes checked storage requirements and validates capacities/alignment before
use. Stage output in inactive bank, commit swap only on successful handler status;
all failures preserve active state/pending token and reset scratch/staging. Enforce
actual OS thread identity inside native APIs. Busy rejects; no queue or real I/O.
Tokens are opaque runtime-issued capabilities including a non-reused runtime
instance identity, mailbox identity and checked sequence; copying a capability is
aliasing it, not minting a new one. No wrap/reuse. Native host is trusted and unsafe
C callers are not a security isolation boundary. Wrong-owner, stale, duplicate and
cross-runtime rejection happen before executing handlers. Preserve bounded last-
completed identity rather than an unbounded history. Disposal is idempotent on the
creator thread and rejects later work. Same-instance program/type state only.

## Scope and ownership
Root owns this plan, module_abi.h initial contract, independent ABI/oracle fixture,
report126, roadmap and integration review/publication. Compiler worker owns
LlvmAot.fs, LlvmToolchain.fs, fsproj resource wiring, and focused module compiler
tests/new test project as agreed. C-runtime worker owns mailbox_runtime.h/.c and
platform support, plus direct C unit tests; no compiler source edits. Integration
worker owns a new native-dispatch experiment, source fixture generation, native
runner and verification script; current report108 experiment remains a reference.
Each worker preserves others' edits and coordinates header/API changes explicitly.
Primary implementation workers use Luna/max. Focused architecture review used
Sol/medium, per user instructions. No worktrees, CI or frozen-runtime rebuilds.

## Acceptance
- One compiled module with initialize/begin/resume; reject cross-program entries,
  unsupported types/effects and duplicate entry names before tool execution.
- Native metadata validates signatures/type IDs/versions/layout and determinism.
- Native process runs A.begin, B.begin/resume, poison/reset scratch, A.failure/retry;
  expected outputs17/103/19 from report108. No managed callback or state decoding.
- Busy/wrong/stale/duplicate/cross-runtime tokens, foreign-thread operations,
  token/generation exhaustion, invalid metadata and short storage reject without
  executing handlers or modifying live state. Test boundary arithmetic explicitly.
- Language and retained-capacity failures preserve old state/token; per-call reduced
  capacity may simulate seven-byte limit without changing physical reservation.
- Zero scratch leases at suspension, balanced acquisition/return, deterministic
  cleanup, no scratch escapes; record high-water used and reserved capacities.
- Exact ABI sizes/offsets checked against independent fixtures. Existing execution
  ABI1/2/3 fixtures remain byte-identical. O0/O2 and direct C sanitization where the
  local toolchain supports it. Native DLL/runner import audit distinguishes allowed
  OS thread/loader imports from CLR/managed dependencies.

## Validation
Fresh local builds; focused new module/dispatch verifier plus
scripts/Verify-NativeConformance.ps1 and scripts/Verify-NativeMailboxSuspension.ps1.
Run direct arena_runtime.c/runtime_test.c O0/O2 and UBSan-trap recipe if C runtime
contracts change; focused C mailbox tests and clang-format for new C. Run applicable
full local solution acceptance after source integration. Record failures and exact
commands/artifacts; do not convert a partial result into a broad completion claim.
Commit, fast-forward main and push only with updated report/evidence and passing
required gates. Real I/O, cancellation buffer ownership, whole-request alternative,
JIT and general release packaging remain later work, not implemented by this slice.
