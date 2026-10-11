# Owning native Bool nominal/refinement slice — read-only plan

2026-10-10. Planning only; no implementation or validation was run. The canonical checkout was on `main` with unrelated untracked trial/script files; preserve them and continue on canonical `main` after the frozen cohort finishes.

## Finding and boundary

This is a bounded extension, not an already supported case. `TypedIR.fs` accepts `IrBool` scalar bases, verifies exact generated wrap/unwrap and pure frozen `Bool -> Bool` calls; `IrInterpreter.fs` represents the nominal key and executes the frozen validator. The owning backend explicitly rejects Bool in `makeProgramInfoForBodies`: the scalar base match near line 837, the validator base guard near 650, and `validateScalarCall` near 1066 accept only Int/String. Its `typeKind` near 2728 likewise maps only Int/String nominals. Host `measureValue`, `encodeValues`, and `decodeValues` near 1290/1400/1500 handle primitive Bool as eight-byte little-endian 0/1 but only Int/String `NamedValue` nominals. Diagnostic wording near 640/1059 must be updated. Existing `BoolTag` negative controls in `tests/AgentLang.Llvm.Tests/Program.fs` near 1840 and 2280 must change deliberately, retaining Float/unsupported-validator negatives.

The selected dynamic descriptor emitter already implements exact TypeIds, frozen validator dependency discovery, constructor invocation, recursive active-input admission and mailbox validation (`OwningStackAot.fs` near 1150, 3060, 3141, 4313, 4637). Wrap/unwrap near 3856 retags the same offset, extent, payload and owner end; no copy is needed. `ArenaLifetime.fs` conservatively treats both operations as unmodeled, so no new transfer optimization is necessary. Primitive Bool has type ID 2, kind 2, fixed payload/extent 8; nominal IDs are distinct by ordinal nominal table order. `owning_stack_runtime.h` already defines Bool kind 2 and layout ABI 3/stack ABI 1. The scanner checks Bool layout shape but its scalar branch in `owning_stack_runtime.c` near 1200 only checks readability: **it currently accepts non-0/1 raw Bool bytes**. This is a concrete extra ingress fix for direct mailbox/raw descriptor safety, including nested active payloads; use a local bounds/encoding check, with no ABI redesign.

## Implementation ownership and sequence

1. `src/AgentLang.Llvm/OwningStackAot.fs`: add `IrBool` to the existing Int/String scalar admission and validator signature guards, generated-call validation and descriptor-kind mapping; add exact-name `NamedValue(name, BoolValue)` branches to host measurement, encoding (0/1), and decoding (reuse primitive Bool decoder so malformed output rejects). Retain nominal TypeIds, the same eight-byte layout, frozen call/revision checks, existing validator false/error distinction, and retag-only wrap/unwrap. No source syntax, interpreter, allocator, or `LlvmAot.fs` change is indicated.
2. `src/AgentLang.Llvm/native/owning_stack_runtime.c`: after readable eight-byte Bool scan, require decoded little-endian value 0 or 1. Keep Int/Unit handling and layout ABI intact. Verify this applies recursively to active records/Option/Result and mailbox preflight; avoid examining inactive sum storage.
3. `tests/AgentLang.Llvm.Tests/Program.fs`: add a focused `--owning-nominal-bool` O0/O2 matrix and a separate refined Bool matrix, then include them in the full suite. Update old BoolTag unsupported assertions; keep Float, effectful/wrong-signature/wrong-target/revision and record-validator rejects. Add direct mailbox initialize/begin/resume and associated RETURN/KEEP checks at O0/O2. Extend native C/unit or independent runner fixtures only where needed for direct malformed byte/sentinel and policy evidence (`src/AgentLang.Llvm/native/owning_stack_runtime_test.c`, `experiments/AgentLang.NativeValueStack/Program.fs`, `experiments/AgentLang.OwningMailbox/*`, `tests/fixtures/native-conformance/*`). Do not rewrite existing Int/String fixtures.
4. `docs/NATIVE-NOMINALS-IMPLEMENTATION.md` and a completion report only after accepted gates; update the explicit Bool gap text and record any retained limits.

## Acceptance oracles

- Two nominal Bool wrappers with equal bits have distinct TypeIds and exact names. A bare `BoolValue`, wrong wrapper, wrong accessor, and wrong frozen generated target reject before publication. Interpreter and native produce the same verified outputs for false/true, construction, unwrapping, calls, locals, equality and matches. The unvalidated type executes no predicate.
- Independent fixture bytes are exactly `00 00 00 00 00 00 00 00` and `01 00 00 00 00 00 00 00`; Option/Result tags use their existing eight-byte prefixes. Descriptor kind is 2, payload/extent/minimum are all 8, nominal TypeId differs from primitive Bool and each peer wrapper. Derive expected IDs from ordered nominal definitions, not native output. Reject raw `02`/negative/all-ones Bool encodings (also nested active cases), preserving ordinary output sentinels and direct mailbox preflight sentinels.
- A frozen pure Bool-to-Bool validator accepts true or false according to its actual body; test one predicate that rejects false and another that rejects true so native cannot hard-code a truth policy. Constructor and raw external input invoke the exact frozen revision. Predicate false yields `REFINEMENT_FAILED`; execution/depth/invalid-result errors remain distinct. Replacement compiler definitions after freezing have no effect. Assert no predicate replay on unwrap, output, inactive alternatives, or KEEP_ASSOCIATED. RETURN re-import validates active retained roots again.
- Nested records, active Option Some, Result Ok/Error and inactive alternatives round-trip exact names and bits. Malformed/failed inputs do not run the callback body. Exercise both arena policies, O0/O2 mailbox callbacks and retry after failure; check committed roots/pending tokens and descriptor-invalid versus structural-preflight sentinel behavior from report 180. Wrap/unwrap add zero deep-copy and move bytes; explicit record/dup operations keep their established counts. Cover short stack/retained capacity and lifetime cleanup.
- ABI/layout audit: preserve layout schema 3, stack ABI 1, owning mailbox ABI 1 and module ABI 1. Audit `al_owning_type_descriptor` kind/type_id and its 36-byte field layout, stack/mailbox context offsets, generated alloca bounds, `tests/fixtures/native-conformance/abi-v3.json`, `mailbox-abi-v1.json`, `module-abi-v1.json` and relevant native-value/mailbox fixtures. No version bump is expected if only kind 2 and distinct existing TypeIds are reused.

## Fresh local validation commands (after implementation)

Run from `D:\code\AgentLang` after the active cohort releases the checkout. Use fresh isolated build artifacts and rebuild native/runtime sources before executing focused runners; never reuse current `bin/obj` products. New focused switches below are implementation deliverables. The newly built DLL path assumes the SDK's standard `--artifacts-path` layout.

```powershell
dotnet build tests/AgentLang.Llvm.Tests/AgentLang.Llvm.Tests.fsproj -c Release --artifacts-path .agentlang/native-bool-investigation/dotnet-artifacts
dotnet .agentlang/native-bool-investigation/dotnet-artifacts/bin/AgentLang.Llvm.Tests/release/AgentLang.Llvm.Tests.dll --owning-nominal-bool
dotnet .agentlang/native-bool-investigation/dotnet-artifacts/bin/AgentLang.Llvm.Tests/release/AgentLang.Llvm.Tests.dll --owning-refined-bool
dotnet .agentlang/native-bool-investigation/dotnet-artifacts/bin/AgentLang.Llvm.Tests/release/AgentLang.Llvm.Tests.dll --owning-refined-mailbox
dotnet .agentlang/native-bool-investigation/dotnet-artifacts/bin/AgentLang.Llvm.Tests/release/AgentLang.Llvm.Tests.dll
pwsh -NoProfile -File scripts/Verify-NativeValueStack.ps1
pwsh -NoProfile -File scripts/Verify-OwningMailbox.ps1 -SerialBuild
pwsh -NoProfile -File scripts/Verify-OwningMailboxPolicy.ps1
pwsh -NoProfile -File scripts/Validate.ps1 -Configuration Release -SerialBuild -SkipPackageAudit -ReportPath .agentlang/native-bool-investigation/full-validation.json
git diff --check
```

The script verifiers create fresh per-run artifact directories. Use a new task directory for the focused `--artifacts-path` build and invoke only its resulting DLL. Execute the exact failing CI command too if CI exposes one. Real-I/O regression is applicable if mailbox fixtures or generated callback path changes beyond Bool admission; use `scripts/Verify-OwningMailboxRealIo.ps1` on the host where loopback is permitted.

Risk: raw malformed Bool acceptance in the runtime scanner means an OwningStackAot-only change would leave a raw Bool validation gap at direct native mailbox ingress. Its new 0/1 check could expose existing malformed primitive-Bool fixtures, which should be corrected only if they were invalid; preserve a negative fixture. Count validator invocations at entry and RETURN carefully to distinguish required re-import validation from accidental replay.
