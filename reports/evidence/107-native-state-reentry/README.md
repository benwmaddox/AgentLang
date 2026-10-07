# Reproduction notes

The canonical full gate is `pwsh -NoProfile -File scripts/Verify-NativeConformance.ps1` from the repository root. It builds fresh isolated artifacts. `full-debug-validation.json` records the 37-check general gate; its accompanying logs retain each check's outcome. Frozen Release research binaries were not rebuilt.

The temporary focused runner is historical evidence, not a new supported test command. To reproduce it, copy `focused-runner/` and `run-state-test.ps1` into `.agentlang/state-reentry-tests/`, build the LLVM test project with `--artifacts-path .agentlang/state-reentry-tests`, then run that script from the repository root. Assertions throw on failure. An app-base dependency resolver is needed because this runner loads the F# test assembly by reflection.

`runtime/direct/run-native-v3.ps1` records the local Windows/Clang commands; its absolute repository/toolchain paths reflect this workstation. Source/objects/executable hashes and separate O0/O2/UBSan logs accompany it. `audit-artifacts.ps1` accepts the final artifact and evidence directories. The published archives are immutable evidence; use a new output directory to reproduce archives.

The first integrated run failed because the new fixture called an unregistered `int.add` primitive. It was corrected to `add`; the focused state test and complete second gate then passed. No failed attempt is counted as a successful gate.

Detailed process-verifier JSON payloads are retained in full-regression-details.zip; the top-level full-debug-validation.json and log remain directly readable.
