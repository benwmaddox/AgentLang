# 225 — Native machine-frame screening

Status: implemented locally; focused validation passed. Not published; report 224's formatting contract remains unresolved.

Historical checkpoint: [follow-up 229](229-canonical-float-text.md) resolves the
Float contract and records the combined milestone's validation. This report
retains the original frame-screen findings and its sealed evidence.

Report 224's large generated Float corpus overflowed the native thread stack. Inspection of its actual O0 COFF object found a 1,599,536-byte fixed prologue allocation in `agentlang_entry_frame`. A 32-input corpus instead has a 27,952-byte O0 fixed prologue and an 872-byte O2 fixed prologue. These measurements describe individual frames, not total live stack usage.

Clang 19.1.5 accepts `-fstack-usage` for LLVM IR and emits adjacent machine-readable `.su` files. The existing 32-input O0 artifact reports `dynamic` usage despite constant allocation sizes: some scratch allocas occur outside the entry block. Its numeric frame size cannot be treated as an upper bound. `-fstack-size-section` produces no usable stack-size section on this COFF target.

The bounded implementation plan has two parts. Emit each distinct fixed-size scratch allocation in its function's entry block, retaining per-call storage and each SSA identity. Then enable fresh stack-usage receipts for owning-backend IR and C objects, reject missing, malformed or dynamic receipts, and reject any individual frame exceeding a prototype limit of 64 KiB before linking/loading. Existing non-owning compilation paths retain their current behavior.

This is frame screening, not a proof that a complete call chain fits the native thread stack. Recursive/generated call depth, helper frames, host interop and available thread-stack space still need a combined budget. Untrusted native execution still requires a worker-process crash boundary. No network interface, arbitrary native invocation or additional language effect is part of this work.

The final focused frame-screen suite passed 27 assertions. It checks valid boundary and duplicate rows; empty, dynamic, unsupported qualifier, extra-column, negative, overflowing and missing-function/location rows; missing receipts; and over-limit frames. Real generated functions report 131,080 bytes at both O0 and O2 and are rejected before linking or invocation. Separate oversized and dynamic C-runtime fixtures are rejected at both optimization levels. Seeded stale receipts are replaced, and seeded stale DLLs are removed on rejection.

Validation also checks allocation placement and existing owning value/mailbox behavior at O0 and O2. The Float formatting semantics decision and failed exact .NET oracle remain open; this work does not relax that oracle or establish an agent-efficacy advantage. The first new test build failed due to multiline fixture indentation; that fixture was corrected without changing assertions, and the failed build log is retained.

| Final focused validation | Result |
| --- | --- |
| Release build | Passed; zero warnings/errors |
| Frame screening | 27 assertions passed |
| Nominal String behavior and allocation placement | 51 assertions passed |
| Refined String behavior and allocation placement | 70 assertions passed |
| Refined mailbox admission | 7 assertions passed |
| String suites' static frame receipts | 45 rows at each optimization; maxima 1,400 bytes O0 / 1,016 bytes O2 |

The full regression gates remain pending with the Float candidate. These focused checks support the bounded change; they do not make the entire combined branch ready to merge.

Independent source review found a malformed-receipt acceptance gap. Requiring both a source location and function name resolved it; new tests retain both counterexamples. The follow-up found no remaining blocking issue in the scoped source review. Module metadata C lacks a separate synthetic fixture but uses the same screened C compiler helper. The review does not establish security of arbitrary native input.

The [candidate and validation archive](evidence/225-native-machine-frame-screening/candidate-and-validation.zip) retains source snapshots, compiler receipts, raw passing/failing logs, synthetic fixtures and the review. The [member index](evidence/225-native-machine-frame-screening/index.json) records SHA-256 and length for all 448 verified archive members. Report 224's earlier sealed archive remains unchanged.
