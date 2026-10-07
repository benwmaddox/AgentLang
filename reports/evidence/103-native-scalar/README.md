# Scalar native milestone evidence

This directory records local validation of the first Windows x64 LLVM scalar
backend. The implementation consumes the existing verified semantic IR.

- `existing-acceptance.json`: isolated Release solution build and 22 existing
  acceptance executables; all exit zero. The exact local runner is preserved
  in `existing-acceptance-command.ps1`.
- `integrated-solution-build.log`: final solution build including both new
  LLVM projects. LLVM tools are not invoked by the build itself.
- `native-conformance.log`: the optional `pwsh -NoProfile -File
  scripts/Verify-NativeConformance.ps1` gate, building into fresh isolated output
  and executing emitted native DLLs at O0/O2.
- `toolchain.json`: installed tools/static support archive paths and SHA-256.
- `frozen-runtime.json`: all nine pinned CLI runtime files and the Business
  assembly remain byte-identical.
- `native-artifacts.json` and `pe-inspection.log`: generated artifact inventory
  and PE-header/import inspection. Native output DLLs have no imports or CLR
  header. The managed wrapper still owns diagnostic metadata.
- `emitted-ir.zip`: exact emitted LLVM input from the final native run; the
  artifact inventory includes hashes of the corresponding objects and DLLs.
- `source-inventory.json` and `validated-sources.zip`: hashes and exact working
  copies of the backend and gate/test inputs, preserving local line endings.
- `index.json`: evidence-file hashes, excluding the index itself.

This is bounded semantic-conformance evidence, not a throughput, memory,
arena-lifetime or agent-reliability result. Native packaging, JIT and mailboxes
remain future work. Local scratch paths in logs identify this run and are not
expected to exist in another clone; rerun the gate to generate fresh artifacts.
