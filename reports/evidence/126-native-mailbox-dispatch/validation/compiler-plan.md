# Native module compiler plan

## Contract
Add `NativeModuleEntry { Name; Body }`, `NativeModuleArtifact` file paths/fingerprint/manifest, and `LlvmAot.compileModule`. Entries are ordinally sorted, require unique nonempty names and the same exact backend-authorized `VerifiedIrProgram` instance, and are fully validated/emitted before clang/lld runs. The API returns files only and does not load the DLL or own runtime state.

## Implementation
- Parameterize the existing emitter's three entry-facing exports so each deterministic entry index compiles to a separate COFF object without changing legacy `compile`/ABI3 behavior.
- Generate immutable module ABI1 metadata C from emitted signatures, diagnostics, and the shared verified program type table; compile it and one copied arena runtime source into the DLL with all entry objects.
- Emit deterministic manifest JSON and SHA-256 fingerprint over ordered entry names, signatures, LLVM IR, diagnostics, and type descriptors; exclude optimization and paths.
- Embed `module_abi.h`, preserve all build inputs/logs, and add focused compiler tests for validation-before-tool, descriptor/layout fixture agreement, build determinism, and existing single-entry behavior.

## Validation
Run the focused `AgentLang.Llvm.Tests` executable and fresh `scripts/Verify-NativeConformance.ps1` after meaningful implementation. Inspect the DLL exports/imports and compare module ABI1 descriptor values to `tests/fixtures/native-conformance/module-abi-v1.json`; record exact commands/results.
