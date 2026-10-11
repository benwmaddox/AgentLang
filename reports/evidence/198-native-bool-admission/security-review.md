# Security review: native Bool milestone 0da7c27

Scope: source-only review of the commit's C descriptor scanner and owning-backend Bool codec/lowering paths. No builds or tests were run, and no trial participant or trial data was inspected.

## Result

No concrete security or correctness regression found in the reviewed Bool boundary changes.

The C scanner checks owner-bounded readability and stack initialization for all eight bytes before reading a Bool, then rejects every little-endian value above 1 (`src/AgentLang.Llvm/native/owning_stack_runtime.c:1200-1216`). The offset/length checks use subtraction bounds before the read, so the new `offset + 4` read does not receive an unbounded offset. Recursive record and active sum scanning reaches the same Bool check; inactive alternatives remain unexamined.

The owning host codec writes Bool as 0/1, and its decoder bounds-checks eight bytes and accepts only those two encodings, including nominal Bool wrappers (`src/AgentLang.Llvm/OwningStackAot.fs:1412-1466`, `1516-1527`, `1584-1592`). External native entry scanning occurs before copying input into the owning stack; semantic refinement validation follows canonical layout admission (`OwningStackAot.fs:4694-4782`). Frozen Bool validators retain exact target/revision and signature checks, and require declared and inferred effects to be empty before reachable validator code is emitted (`OwningStackAot.fs:646-687`, `1140-1183`). I found no Bool-specific path that bypasses those checks or introduces a host capability.

## Limits

This was a bounded source review of the committed milestone, not a general security certification. Runtime and conformance tests were not rerun, as requested.
