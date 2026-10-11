# 194 — Next native slice: Bool wrappers and admission

Status: reviewed implementation plan only. No runtime changes, builds or tests
were performed for this report. The frozen maintenance cohort remains open.

The next bounded native slice admits Bool-backed nominal wrappers and frozen
pure Bool-to-Bool refinements in ordinary owning execution and mailbox entry.
Typed IR and the interpreter already represent these types; the owning backend
currently rejects them. Preserve exact nominal identities, frozen validators,
retag-only wrap/unwrap, and existing atomic failure behavior.

Read-only inspection also identified a raw Bool validation gap: the native
descriptor scanner checks readable eight-byte payloads but does not restrict
their value to 0 or 1. Add that check for primitive and nominal Bool admission,
including active nested payloads. This is a source finding awaiting executable
negative controls, not an exploit finding or a security certification.

Implementation ownership is the owning AOT backend, native descriptor scanner
and focused LLVM/native tests. Reuse layout schema 3, stack ABI 1, mailbox ABI 1
and module ABI 1; audit their offsets, descriptors and fixtures together. No
allocator redesign, source syntax, new host effect or dependency is proposed.

Acceptance requires O0/O2 parity for false/true and peer nominal types; exact
eight-byte encodings; rejection of noncanonical bits and wrong identities;
nested record/Option/Result admission; validators that independently reject
false or true; no valid output or committed-state publication on failure; both
mailbox arena policies; and zero payload moves for wrapping/unwrapping. Run fresh focused
and full local gates before reporting implementation complete.

A subsequent [read-only admission review](evidence/194-native-bool-implementation-plan/admission-review.md)
confirms the leaf-scanner gap and narrows its fix: decode all eight bytes only
after the existing owner-bounded readability check. Add direct scanner tests
for high-byte noncanonical values, short buffers and malformed active nested
payloads; host Bool encoding alone cannot produce these negative inputs.
Preserve the existing failure contract: scanner result canaries remain untouched
on failure, while mailbox output descriptors become invalid after successful
structural preflight. Require no valid output or committed state, rather than
unchanged mailbox descriptor bytes at that stage. Nominal identity belongs to
typed admission and emitted TypeIds, not to the eight payload bytes.
The [addendum index](evidence/194-native-bool-implementation-plan/admission-index.json)
pins this source inspection separately; the original plan and index are retained.
No executable validation or additional runtime change is claimed.

The [reviewed plan](evidence/194-native-bool-implementation-plan/plan.md)
contains file/function boundaries and validation commands. The
[source-pin index](evidence/194-native-bool-implementation-plan/index.json)
records the inspected code identities. Begin implementation in canonical main
after the frozen cohort and its representation-adapted check are closed. Keep
agent efficacy, semantic conformance and native service performance as separate
claims; Float/List, general JIT and service memory-policy selection remain gaps.
