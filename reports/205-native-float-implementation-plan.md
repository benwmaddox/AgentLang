# 205 — Native Float implementation plan

Status: reviewed source-only plan. No Float runtime implementation or Float validation was performed. The planning subagents performed no Git operations; this report is committed with milestone 204.

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

## Frozen scope and review corrections

The scope is current owning AOT only; legacy shared-graph AOT and
`float.to-string` remain explicitly unsupported. This is a plan, not implemented
Float support. The semantic IR remains authoritative for all backends.

The independent review below is part of the implementation contract and supersedes
any weaker or incomplete wording above. Freeze the entry diagnostic as
`IR_BACKEND_ENTRY_ARGUMENT_NONFINITE`, word = executionName, span = None,
Expected = ["finite Float"], deterministic Actual, after argument count/root
selection and before body fuel or host hooks. Native conversion failures retain
the original input bits, including for `float.round`; clear the diagnostic payload
deterministically. Use the interpreter's actual formatting path rather than a
new assumed shortest-roundtrip contract.

Insert Float after String when assigning IDs: existing primitive, nominal and
String IDs can remain stable; compound IDs may shift. Include release runtime
size/offset assertions, mailbox kind-range consumers and Verify-OwningMailbox in
the ABI audit. Replace both fixed and dynamic bytewise equality sites, including
String-containing aggregates and empty records. No implementation begins before
these contracts and independent fixture expectations are accounted for.

## Independent plan review (source only)

Scope is the current `OwningStackAot` backend: finite numeric Float transport, primitives, aggregate equality, and Float refinements. The legacy `LlvmAot` ABI-v3 backend stays explicitly unsupported. Defer `float.to-string` until its invariant interpreter spelling has a tested deterministic native equivalent. No user decision is needed for these defaults.

## Required corrections and implementation constraints

1. **Exact diagnostics require a value channel for range failures.** `IrInterpreter.fs` uses `string value` for `Actual` in `RUNTIME_NONFINITE_FLOAT` and `RUNTIME_RANGE` (`float.to-int` and `float.round`). The owning native path currently returns only `ErrorId` and reconstructs a frozen `Diagnostic` (`OwningStackAot.fs`, `diagnosticForError`). The arbitrary finite input to either conversion cannot be encoded in a finite table of diagnostic IDs. An appended 64-bit Float-bits field in `al_owning_stack_context` / `NativeOwningContext`, a metadata marker for diagnostics that consume it, and managed reconstruction using the **same formatting path as the interpreter** are justified for complete parity. Set/clear the field deterministically on begin/failure, and capture the original input for both range failures; `float.round` reports the original value, not the rounded result. Arithmetic overflow can only yield signed infinity from finite operands here, but should use the same channel for a uniform exact diagnostic. `float.divide` checks either signed zero before division and has empty `Actual`. Match code, message, word, span, `Expected`, `Actual`, and step charge. Do not silently claim full parity if only code/span is compared.

2. **Version all consumers of the changed context and layout.** Append `AL_OWNING_TYPE_F64 = 9`, keeping the existing descriptor shape and kind values; bump owning layout ABI/schema 3 to 4. Appending the context field changes its size (168 bytes today), so bump owning stack ABI 1 to 2, update C/F# definitions and direct fixtures, and bump owning mailbox module ABI 1 to 2 because callbacks use that context. `mailbox_runtime.c` has a separate owning type-kind range ending at `AL_OWNING_TYPE_RESULT`; extend it, along with the owning scanner's descriptor validation. Also update `owning_stack_runtime_test.c` size/offset assertions and emitted metrics, `owning_release_runtime_test.c` size/offset assertions, and applicable direct mailbox/bank tests. `scripts/Verify-OwningMailbox.ps1` reads the stack ABI header and belongs in the version audit. Fixture versions occur in `native-value-stack.json` (including `contextSizeBytes`, offsets, and a second layout ABI field), `owning-mailbox.json`, `owning-mailbox-sums.json`, and `owning-mailbox-refinements.json`; update each field that exists in each fixture. Keep shared-graph `NativeAbi.Version = 3`, generic module ABI, and its `abi-v3.json`/record/scalar fixtures unchanged.

3. **Preserve type identity without promising all IDs stay fixed.** `makeProgramInfoForBodies` assigns Int/Bool/Unit IDs 1/2/3, nominal IDs from 4, String after nominals, then compound IDs. A dedicated Float ID can be inserted after String while preserving those existing primitive/nominal/String IDs; compound IDs may shift. Tests should check deterministic IDs, distinct Float versus Int and distinct Float nominal wrappers, and existing IDs for Float-free layouts where feasible. Descriptor *kind* and TypeId are different contracts. Scalar Float wrappers need their own TypeId even though their leaf kind is F64.

4. **Replace both bytewise equality call sites.** Fixed-layout and dynamic-layout lowering independently call `al_owning_equal`, which compares bytes; both must use typed equality. A safe helper takes a validated layout/type index and bounded owners for both values, checks each extent, and recursively walks declared record fields or only the active Option/Result case. Float leaves compare numeric equality (`+0.0 == -0.0`); other leaf encodings may retain exact comparison after validation. Enforce depth 64, type/field limits, checked offset addition, and owner bounds; avoid scanning inactive sum payload bytes or whole aggregate bytes when any reachable leaf can be Float. Existing verified equal operand types and distinct nominal TypeIds provide nominal compatibility; the native helper must not infer type from raw equal-sized bytes. Cover dynamic String-containing aggregates and zero-width empty records as well as fixed Float records.

5. **Finite scanner validation must happen inside the owner.** `al_owning_scan_value` already gates scalar reads with `al_owning_scan_readable_in_owner`; add F64 to descriptor scalar rules and the leaf branch, read only after the eight-byte owner check, then reject exponent-all-ones (both infinities and NaNs). Keep signed zeros, subnormals, and max finite. Exercise 0–7 byte source and owner lengths, nested active Float leaves, and inactive None/other Result-case bytes. Host measurement, encoding, native import, and managed decode must reject non-finite values at their own boundaries; output staging must remain atomic.

6. **Match numeric semantics at the exact boundaries.** Interpreter `float.to-int` truncates toward zero after checking `[-2^63, 2^63)`; `float.round` applies `Math.Round(value, MidpointRounding.AwayFromZero)` then checks that same half-open range. Use strict LLVM double operations without fast-math, an ordered zero check before `fdiv`, and a range guard before `fptosi`; never execute out-of-range `fptosi`. Preserve signed-zero bits in transport. Check non-finite arithmetic results after each operation. The interpreter's `float.to-string` is `ToString("G", InvariantCulture)`; no platform `%g` mapping is in scope.

7. **Specify Float entry validation.** `IrEntryArgument` has no Float case today. Add one and reject NaN/infinity during argument preflight after count/root selection and before body fuel or host hooks. Choose and freeze an explicit entry diagnostic (for example `IR_BACKEND_ENTRY_ARGUMENT_NONFINITE`, `Word = executionName`, no span, `Expected = ["finite Float"]`, `Actual` rendered deterministically), rather than allowing non-finite values to enter `RuntimeFloat` and fail later as an arithmetic diagnostic. Add direct interpreter tests for the choice. The real project path exists: `tests/AgentLang.IR.Interpreter.Tests/AgentLang.IR.Interpreter.Tests.fsproj`, so `dotnet run --project tests/AgentLang.IR.Interpreter.Tests/AgentLang.IR.Interpreter.Tests.fsproj` is the correct focused command.

## Acceptance and validation

Keep the plan's O0/O2 bit-exact result and complete diagnostic parity gates, including instruction fuel, constructor and host-input refinement paths, both signed zeros, min/max conversion boundaries, subnormals, active/inactive sum cases, and mailbox Float state across initialize/begin/resume. Add exact TypeId/kind checks, fixed and dynamic typed equality cases, owner-bounded C scanner negatives, and context/ABI fixture checks. The current `scripts/Verify-NativeValueStack.ps1` has a frozen `FloatTag` rejection and layout ABI oracle; update those independent expectations and dynamic C counts while preserving the fixed baseline. Run that script, `scripts/Verify-NativeConformance.ps1`, `scripts/Verify-OwningMailbox.ps1`, and the interpreter project command after implementation. Audit the final diff for legacy-backend changes and remaining bytewise Float aggregate paths.
