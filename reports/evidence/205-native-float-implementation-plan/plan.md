# Native Float implementation plan

Status: source-only plan. No build, tests, runtime edits, or Git operations were performed.

## Decision and scope

Implement first-class finite `Float` values in the current owning value-stack AOT path, using the existing verified semantic IR and interpreter as the oracle. A Float is an IEEE-754 binary64 value stored as its exact eight little-endian bytes in the existing eight-byte scalar slot. Admit Float literals, direct entry values, outputs, locals, calls, record fields, Option/Result payloads, and nominal Float wrappers. Keep Float type and wrapper identities distinct in descriptors and host values.

Support the existing numeric operations whose semantics are straightforward to preserve in LLVM: `float.add/subtract/multiply/divide`, four Float comparisons, `equals`, `int.to-float`, `float.to-int`, and `float.round`. Do not add a second IR or interpret only a pass-through fixture. `float.to-string` is a separate formatting contract: the interpreter uses invariant .NET `G` formatting, so do not map it to platform `%g` without proving byte-for-byte spelling parity. Keep that primitive explicitly unsupported in this milestone unless product scope requires all canonical Float primitives.

The old shared-graph `LlvmAot` path is separate from `OwningStackAot` and still has its own arena ABI, primitive whitelist, and `NativeProgramMetadata` type universe. Recommendation: keep its ABI-v3 Float exclusion intact for this milestone and claim support only for owning AOT. If “native Float” is intended to include both AOT backends, decide that before implementation; it requires a separate expansion of `NativeAbi.fs`, `NativeValueMetrics.fs`, `NativeValues.fs`, `LlvmAot.fs`, `arena_runtime.h/.c`, and the module/runtime ABI fixtures.

## Existing semantics and missing paths

`Core.fs`, `TypedIR.fs`, `FlowParser.fs`, `FlowLowering.fs`, and `IrInterpreter.fs` already represent `Float`, finite literals, finite arithmetic results, Float comparisons/conversions, and structural equality. The verifier rejects non-finite Float constants. Interpreter `equals` uses value equality, so `+0.0 = -0.0`; Float division checks either signed zero first and reports `RUNTIME_DIVIDE_BY_ZERO`; arithmetic producing infinity reports `RUNTIME_NONFINITE_FLOAT`; `float.to-int` and `float.round` use `RUNTIME_RANGE`. Preserve these exact operation order, codes, messages, spans, and fuel behavior.

The entry contract is not complete: `IrEntryArgument` in `IrInterpreter.fs` has only Int/Bool/Unit primitive cases. Add `FloatArgument of double`, map it to `RuntimeFloat`, and reject NaN/infinity at entry before charging body fuel. The owning host API already accepts a general `Value list`, but `OwningStackAot.fs` measurement/encoding/decoding has no `FloatValue` case; add a finite check on host encoding and a finite check on decode. Use `DoubleToInt64Bits`/`Int64BitsToDouble` and preserve signed zero exactly.

In `OwningStackAot.fs`, `typeName`, `langTypeFor`, primitive type IDs, `buildType`, scalar base admission, primitive whitelist/signature validation, Float constants, encoding/decoding, descriptor-kind generation, and the fixed-module and mailbox LLVM lowering all omit `IrFloat`. The refined scalar validator gate accepts only Int/Bool/String; extend it to Float while retaining the exact frozen pure `Float -> Bool` target/revision checks, generated constructor identity, wrap/unwrap retagging, host-root validation, and validator fuel accounting.

The generated equality path always calls `al_owning_equal`, which compares bytes. That makes signed zeros unequal and also makes any Float nested under a nominal wrapper, record, Option, or Result potentially unequal by representation. Replace it with descriptor- and type-directed semantic equality for supported layouts. Compare Float leaves using IEEE ordered equality; recurse through only the active Option/Result case and compare record fields by their declared type. Keep nominal compatibility enforced by verified operand types and distinct descriptor/type IDs. Do not leave an aggregate bytewise shortcut for types that contain Float.

## Lowering and diagnostics

Use strict LLVM `double` operations with no fast-math flags. Load the scalar's i64 bits, bitcast to `double`, perform the operation, then bitcast the result back to i64 for the existing owning store/move machinery. Check Float divide-by-zero before `fdiv` with an ordered comparison against zero (both signed zeros match). After arithmetic, reject any result whose exponent bits are all ones; do not reject finite subnormals or underflow to signed zero. This avoids executing a divide-by-zero and matches the interpreter's finite-result rule.

Implement `int.to-float` with signed `sitofp`; guard `float.to-int` before `fptosi` using the interpreter's half-open Int64 range `[-2^63, 2^63)`; implement `float.round` with the LLVM round-away-from-zero intrinsic and range-check the rounded value before conversion. Return `RUNTIME_RANGE` for conversion range errors. Compare `+0.0` and `-0.0` as equal, but preserve the input/output sign bit when returning or storing either zero.

For full `Diagnostic` parity, Float non-finite/range failures need a dynamic Float value in `Actual`; current owning diagnostics carry only a diagnostic ID. Add an appended 64-bit diagnostic value field to `al_owning_stack_context`/`NativeOwningContext`, set it only for the Float diagnostics that need it, and let the frozen diagnostic metadata identify how to render it. Decode that field with the same .NET formatting path as the interpreter. Divide-by-zero has no dynamic actual. Preserve output staging atomicity and instruction charging before failure. If the implementation deliberately compares only code/word/span and omits `Actual`, report that as a semantic limitation rather than claiming exact interpreter diagnostic parity.

## ABI and fixture audit

Add an appended owning layout kind, for example `AL_OWNING_TYPE_F64 = 9`, without changing descriptor sizes or offsets. Bump owning layout ABI/schema 3 to 4 in `owning_stack_runtime.h` and the generated LLVM layout constant and managed `LayoutSchemaVersion`.

Because the diagnostic payload changes `al_owning_stack_context`, bump owning stack ABI 1 to 2 and update the C/F# context declarations, direct context fixtures, and ABI checks together. The owning mailbox callbacks share that context and layout, so bump the owning mailbox module ABI 1 to 2 as well. Mailbox control ABI structures do not change. The older shared-graph `NativeAbi.Version = 3` and generic `AL_MODULE_ABI_VERSION = 1` remain untouched under the recommended scope.

Audit all current owning fixtures and generated metrics: `tests/fixtures/native-conformance/native-value-stack.json` (fixture schema plus `layoutAbiVersion`/`layoutSchemaVersion`), `owning-mailbox.json`, `owning-mailbox-sums.json`, and `owning-mailbox-refinements.json`. Keep existing Int/Bool/Unit/nominal/String TypeIds stable where practical; assign Float deterministically without renumbering old primitive and nominal IDs. Update `scripts/Verify-NativeValueStack.ps1`'s layout/version assertions, old owning Float rejection expectation, unsupported-kind assertions, and dynamic C-case/check oracle counts. Keep its frozen fixed C baseline unchanged. Leave `scalar-cases.json`, `record-cases.json`, `abi-v2.json`, and `abi-v3.json` unchanged if the old shared-graph backend remains out of scope; those fixtures explicitly describe that separate backend.

## Files and ownership

- `src/AgentLang.Core/IrInterpreter.fs`: Float entry argument and pre-execution finite validation; no new IR semantics.
- `src/AgentLang.Llvm/OwningStackAot.fs`: Float eligibility, layout/type IDs, validators, host codecs, generated descriptor kind, strict arithmetic/conversion lowering, and typed equality calls.
- `src/AgentLang.Llvm/native/owning_stack_runtime.h` and `.c`: new Float leaf kind, owner-bounded finite scanner check, stack ABI context payload, and descriptor-aware equality.
- `src/AgentLang.Llvm/native/owning_stack_runtime_test.c`: direct raw Float scanner/equality negative and boundary cases.
- `src/AgentLang.Llvm/native/owning_mailbox_abi.h` and `mailbox_runtime.c`: mailbox ABI version and context/layout integration checks only if required by the stack ABI bump.
- `tests/AgentLang.Llvm.Tests/Program.fs`: interpreter/owning AOT parity at O0/O2, entry, conversions, arithmetic errors/fuel, refined types, equality, and mailbox state-field coverage.
- `tests/fixtures/native-conformance/native-value-stack.json` and the three owning mailbox fixtures above; `scripts/Verify-NativeValueStack.ps1` for independent schema/count assertions.
- Update the eventual implementation report and the Float-deferred roadmap note after validation; this planning task does not edit either file.

## Acceptance gates

1. At O0 and O2, compare interpreter and owning AOT values by exact Float bits for finite literals, direct inputs/results, locals, user calls, branches, conversions, and record/Option/Result transport. Include both signed zeros, subnormal values, and largest finite values. Verify per-instruction fuel for success and each failure, including a refined validator call.
2. Compare complete structured diagnostics for `float.divide` by `+0.0` and `-0.0`, `0.0/0.0`, positive and negative arithmetic overflow, and `float.to-int`/`float.round` range failures. Arithmetic overflow must use the interpreter's `RUNTIME_NONFINITE_FLOAT`; conversion overflow must use `RUNTIME_RANGE`. Check the source word/span, expected/actual fields, charged fuel, and that no partial output is published.
3. In direct C scanner tests, accept positive/negative zero, finite subnormals, and max finite. Reject quiet/signaling NaNs and both infinities after an owner-bounded eight-byte check. Exercise every source/owner length 0–7, active malformed Float in record/Some/Ok payloads, and inactive bytes after None/Error tags; failure must preserve output sentinels and never read beyond the owner. Verify host codec rejects non-finite `FloatValue` and output decode refuses malformed Float bytes.
4. Test direct Float equality and `+0.0 == -0.0`; equal same-nominal Float wrappers with opposite zero signs; false equality when another record field differs; and nested record/Option/Result equality with Float leaves. Define the supported equality boundary explicitly. Verify two Float-based nominal wrappers keep different TypeIds and cannot be substituted, and run both a passing and failing pure frozen Float validator through constructor and host-input paths.
5. Exercise a Float field in owning mailbox state through initialize/begin/resume under both arena policies if mailbox support is included. Ensure malformed Float bytes fail preflight before fuel or output publication.
6. Update independent fixture oracles before implementation changes; add Float cases to the dynamic C suite only, preserving the fixed baseline and matching exact O0/O2 case/check counts.

## Existing validation commands

Run these repository gates after implementation:

```powershell
pwsh -NoProfile -File scripts/Verify-NativeValueStack.ps1
pwsh -NoProfile -File scripts/Verify-NativeConformance.ps1
dotnet run --project tests/AgentLang.IR.Interpreter.Tests/AgentLang.IR.Interpreter.Tests.fsproj
```

`Verify-NativeValueStack.ps1` builds/runs the direct owning C suite at O0/O2 (and its configured trap checks), checks stable source hashes and independent fixture counts, and exercises the owning-stack experiment. `Verify-NativeConformance.ps1` builds and runs the full focused LLVM/native test executable, including interpreter parity and mailbox tests. Report 202 records that the default build can be blocked by NuGet audit service warnings on this host; its successful full-test fallback was a fresh serial, audit-disabled build (`-m:1 -p:NuGetAudit=false`) followed by the produced test DLL. Keep that environment adjustment visible in the validation record.

## Product decision to settle before implementation

Confirm whether this milestone means Float support in the current owning-stack AOT backend only (recommended), or in both owning-stack and legacy shared-graph AOT. Also confirm whether `float.to-string` must be included now; preserving its invariant shortest-roundtrip spelling needs a deterministic formatting implementation. The proposed default keeps the older graph backend and `float.to-string` explicitly unsupported, while fully supporting finite numeric Float transport, arithmetic, comparisons, equality, conversions, refinements, and typed aggregates in owning AOT.
