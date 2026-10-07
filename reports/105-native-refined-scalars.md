# Native refined scalar conformance

Status: implemented and locally validated, 2026-10-07.

## Decision and scope

The arena comparison in report 104 supports further integration, but the native
backend previously lacked domain scalar and record values. This milestone adds
refined nominal scalars through existing verified IR before reference-bearing
record layouts. This is a useful strong-type execution milestone, not evidence
of arena lifetime safety and not a new memory semantic model.

Support Int- and Bool-backed nominal scalars, their constructors and
unwrappers, typed equality, local values, branches and calls. Constructors use
the validator captured in the immutable verified program. Validator success,
false refinement and failures must agree with interpreter semantics, including
source metadata, constructor aliases, depth and instruction limits. Validate
all reachable dependencies before compilation, including untaken branches.
Unsupported records, Float, String, containers and effects continue to reject.
Plain Unit remains supported; current language rules do not permit Unit-backed
nominal scalars, and this backend milestone does not change that rule.

Keep the semantic IR unchanged. Nominal identity lives in the frozen program
and emitted value types; one native payload still occupies an eight-byte slot.
Audit ABI v1 and its independent layout fixture, including scratch capacity for
validator outputs when the entry returns no values. Host decoding must restore
the original nominal type rather than expose a bare integer.

## Acceptance and validation

- Execute fresh O0/O2 native code against interpreter results and independent
  expected values/diagnostics for plain and refined nominal values.
- Exercise valid/invalid constructors, validator failures, generated aliases,
  equality, nominal outputs, calls/locals/branches and frozen validator versions.
- Reject unsupported types and validator closures before compiler invocation.
- Preserve exact call-depth, instruction-count and error-source behavior.
- Run the native gate and focused IR/interpreter regression checks using isolated
  build outputs; preserve frozen agent-research Release binaries.
- Review code, report results and evidence, then publish together.

## Results and limits

The exact optional native gate passed with 252 assertions at O0/O2. Fresh builds
reported zero warnings and errors. Focused typed-IR verification passed 114
assertions and interpreter conformance passed 31 assertions, each from isolated
Release build outputs. The frozen nine-file CLI runtime and Business assembly
remain byte-identical. No Core language, verifier or interpreter code changed.

Native execution now returns named Int/Bool values, runs frozen refinement
validators, preserves generated constructor aliases, and agrees with interpreter
results and complete diagnostics. Independent fixtures check expected values,
refinement messages and expected/actual states, arithmetic failures, constructor
spans, and depth/fuel boundaries. Tests exercise a same-name validator replacement:
after a replacement snapshot uses the new same-name revision, the original
verified snapshot still compiles and executes with its captured validator.
Zero-output entries still reserve validator scratch space.
Unsupported underlying types, records, effects and untaken unsupported validator
branches reject before native compilation.

The physical ABI stays at version 1: 32-byte context, eight-byte scalar slots,
four-byte status and unchanged field offsets. The ABI fixture now explicitly
records nominal Int/Bool support. Type identity is frozen compiler/artifact
metadata, not an extra runtime tag stored beside every scalar. This does not yet
provide compiler-free release metadata packaging or native record storage.

Independent review caught incorrect test expectations, a branch fixture that
was not actually untaken, an unused replacement snapshot and missing depth/fuel
boundary cases. Local execution caught LLVM percent escaping and fixture type/
diagnostic mismatches. Those were corrected before accepting the complete gate;
no assertion was weakened to treat a failing behavior as success.

Run `pwsh -NoProfile -File scripts/Verify-NativeConformance.ps1` to reproduce the
native gate. [Saved evidence](evidence/105-native-nominal/index.json) includes the
captured terminal output, regression logs, source/fixture hashes and archive,
generated LLVM archive and artifact inventory, toolchain identity and frozen
runtime checks. Validation was local; CI remains manual-only.

This is conformance evidence for native strong types. It neither establishes a
comparative agent-reliability advantage nor proves arena lifetime safety. Float,
String, records, containers, effects, JIT and native mailbox execution remain
outside this native slice. Current source rules reject Unit-backed nominal types;
plain Unit execution remains supported.

## Next reference-bearing memory boundary

After nominal scalars, native nested records are the first useful arena fixture.
Use one scratch region for an entry invocation and its nested calls; returning
from a function does not reset that region. Before releasing scratch, export
successful output roots to a separate valid owner with bounded, atomic promotion
that preserves nominal identity and sharing. Reuse must not invalidate earlier
outputs. Define a versioned handle/layout contract and deterministic ABI oracle.

That step should extend actual language execution, not a parallel mailbox model.
It does not require source-level allocation instructions initially. Same-mailbox
suspension, real I/O, cancellation ownership and code-generation lifetime remain
subsequent integration requirements. The final runtime language remains open;
maintainability and conformance govern that decision.
