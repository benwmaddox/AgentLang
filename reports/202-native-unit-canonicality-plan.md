# Next owning-runtime slice: canonical Unit ingress

Status: source-reviewed proposal; not implemented or runtime-tested. This is a
small runtime conformance correction before admitting another language type.
It does not reopen the closed authoring pilot or claim an agent-edit result.

## Decision

Make the owning native scanner reject every Unit slot whose eight-byte value
is nonzero. The managed codec already defines the canonical representation:
`OwningStackAot.fs` writes zero at line 1417 and rejects nonzero bytes while
decoding at lines 1528-1531. The raw C scanner checks the full Unit range is
readable, but then accepts any bits (`owning_stack_runtime.c`, lines
1200-1217). A direct native ingress can therefore admit bytes the managed
boundary rejects. The interpreter has one `RuntimeUnit` value, and its
`equals` operation is structural (`IrInterpreter.fs`, line 797); the owning
equality helper compares payload bytes (`owning_stack_runtime.c`, lines
2137-2149). The raw C boundary can therefore accept two noncanonical
`Some Unit` encodings with different tokens, then report them unequal by
bytes even though Unit has one semantic value. Managed callers reject these
encodings during decode; the mismatch is specific to direct native ingress.

Keep the change in the shared scanner. After its existing owner-bounded
eight-byte readability check succeeds, read Unit and require the full
little-endian `uint64` to be zero. Use the existing scanner failure path and
do not publish measured payload/extent on rejection. This makes external and
stack-owned recursive scans agree without teaching generic equality to
interpret malformed representations.

Preserve sum behavior: inspect only the active child. `Some Unit` with a
nonzero active token must fail. `None` has an eight-byte tag and extent; bytes
after that extent are inactive storage and remain unexamined, even if they
look like a malformed Unit. Add a C `Option<Unit>` case that proves both
behaviors, as well as short-source and short-owner rejection before any
out-of-owner read.

## Scope and compatibility

- Production change: only `src/AgentLang.Llvm/native/owning_stack_runtime.c`.
  Add C coverage in `src/AgentLang.Llvm/native/owning_stack_runtime_test.c`.
  Add a focused canonical-Unit interpreter/native O0/O2 case to
  `tests/AgentLang.Llvm.Tests/Program.fs` so the language oracle and native
  execution continue to agree on `Unit` and `Option<Unit>` values.
- No descriptor kind, TypeId, field layout, or struct size changes. Keep
  owning layout schema 3, stack ABI 1, mailbox ABI 1, and module ABI 1. Do not
  relabel historical ABI fixtures or change generated layout snapshots.
- This tightens acceptance only for malformed raw Unit bytes. The managed
  encoder emits zero already, so valid source values, signatures, and
  serialized layouts remain unchanged. It does not change Unit semantics,
  effects, allocation, or equality implementation.

## Acceptance gates

- In the C scanner suite, zero Unit passes at external ingress and in an
  owner-bounded stack value; representative nonzero patterns fail. Verify
  every truncated external source size from zero through seven and a
  corresponding short-owner case fail before load. In
  `Option<Unit>`, active `Some` with nonzero token fails, active `Some` with
  zero passes, and `None` with nonzero trailing inactive bytes still measures
  as payload/extent 8/8. Confirm output sentinels and stack guard bytes remain
  unchanged on failure.
- The LLVM test project runs the same canonical Unit and `Option<Unit>` cases
  through the interpreter and owning O0/O2, checking decoded values and
  instruction fuel. Keep malformed raw-byte rejection in direct C scanner
  tests: the managed host API cannot construct a noncanonical `UnitValue`.
- `scripts/Verify-NativeValueStack.ps1` builds and runs the C suite at O0 and
  O2 and compares its metrics with the independent
  `storageRuntimeTestOracle` in `tests/fixtures/native-conformance/native-value-stack.json`.
  Its current source oracle is 51 cases / 667 checks total, split into a
  frozen fixed baseline of 16 / 189 and 35 / 478 dynamic cases. Register the
  Unit test as one dynamic case, count its explicit `AL_CHECK`s from the test
  source, and update only the corresponding dynamic and total case/check
  counts. Preserve the fixed baseline and all ABI/layout metric fields. The
  verifier must show exact O0/O2 agreement with the updated independent
  counts and stable hashed source inputs.
- `scripts/Verify-NativeConformance.ps1` builds and runs the full focused LLVM
  test executable, including interpreter/native parity. Review the test
  additions against the interpreter's single-Unit semantics and managed
  codec's zero-byte check; do not derive expected behavior solely from the
  C implementation under test.

## Why this slice precedes Float or List

This closes a real mismatch with one eight-byte validation in an existing
scanner; it requires no new type surface, ABI, allocator, or runtime
representation. A Float pass-through slice would add a type kind and layout
ABI version, plus a shared entry contract: the interpreter entry API has no
direct Float argument today. Source parsing and IR verification require
finite Float, and arithmetic rejects non-finite results, but signed zero is
valid. Since interpreter equality treats `+0.0` and `-0.0` as equal while
owning equality is bytewise, Float equality must stay rejected for Float and
any Float-containing type until comparison is semantic. Immutable List needs
dynamic sequence layout and operations over variable offsets, so it is a
larger slice. Revisit Float transport only after defining the common entry
path and retaining this signed-zero restriction.
