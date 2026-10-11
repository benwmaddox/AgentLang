# 224 — Native Float formatting findings

Status: candidate implementation; not accepted for publication. Formatting contract decision pending. Main's previous accepted checkpoint is report 223.

Historical checkpoint: the status above describes the captured candidate.
[Follow-up 229](229-canonical-float-text.md) selects canonical shortest-roundtrip
text and records the subsequent implementation and validation. The failed
candidate evidence below remains unchanged.

The owning AOT candidate implements bounded, integer-only `float.to-string` and owned UTF-16 output. Validation uncovered two issues that prevent treating this as a completed native milestone: .NET's existing interpreter formatting contract is not always round-tripping, and a large generated function crashed the in-process native test host. No throughput or agent-efficacy advantage is established.

## Formatting counterexample

For finite Float bits `0410000000000000`, the interpreter's .NET 9.0.11 invariant `G` formatter emits `4.104536801298376E-289`. Parsing that text produces `040FFFFFFFFFFFFF`, the preceding Float. The native candidate emits `4.1045368012983762E-289`, which parses back to the original bits. The same defect is observed in a separately recorded PowerShell/.NET 10.0.11 probe.

The original plan incorrectly assumed exact .NET `G` compatibility and mathematical shortest-roundtrip output were identical requirements. The full focused corpus disproved that assumption. Its exact .NET oracle remains failing; no expectations were relaxed and no value-specific workaround was added.

A likely cause appears in [.NET 9 Dragon4](https://raw.githubusercontent.com/dotnet/runtime/v9.0.10/src/libraries/System.Private.CoreLib/src/System/Number.Dragon4.cs): its unequal-margin check shifts a 32-bit `1U` by the Double mantissa width of 52. That shift is masked to 20 rather than producing the 64-bit significand. The [Grisu3 boundary path](https://raw.githubusercontent.com/dotnet/runtime/v9.0.10/src/libraries/System.Private.CoreLib/src/System/Number.DiyFp.cs) uses a 64-bit literal. Output therefore depends on whether formatting falls back to Dragon4. Replacing every power-of-two margin with the erroneous symmetric margin would introduce additional errors.

Recommendation: explicitly define invariant round-tripping Float text, update the interpreter and native backend together, and validate against an independent oracle. The alternative is to preserve today's output and defer native formatting. This is a language-semantics choice, not a test fix; the user preference is pending.

## Candidate implementation and evidence

The original Dragon4-style helper uses exact interval arithmetic, five 40-limb workspaces (800 bytes), at most 324 decimal-normalization shifts and 17 significant digits. It stages bounded ASCII text before checked UTF-16 publication and records allocation traces. Generated-frame metadata accounts for a 32-byte text buffer plus a four-byte length. The helper's transient C scratch is separate from that metadata.

No package, language effect grant, arbitrary .NET invocation, locale formatter, managed callback or runtime heap dependency was added. Owning stack ABI 2, layout/schema 4, mailbox module ABI 2 and the 176-byte context are unchanged. Candidate verifier changes pin the helper header and enable package auditing. These changes remain on the local candidate branch.

| Check | Observed result |
| --- | --- |
| Unchanged dependency restore with NuGet auditing enabled | Passed, no warnings observed |
| Focused serial Release build | Final candidate rebuilt successfully, zero warnings/errors; [fresh build receipt](evidence/224-native-float-format-findings/final-candidate-build-receipt.json) and [raw log](evidence/224-native-float-format-findings/final-candidate-build.log) preserved separately from the sealed archive |
| Focused native exact .NET `G` corpus | 30,396 finite values at O0 and O2; final parity assertion failed |
| Independent CPython 3.12.10 shortest-decimal oracle, rendered with intended notation rules | 38,346 O2 values; zero text mismatches and zero roundtrip failures |
| Native C tests, including six regression vectors around the counterexample | O0, O2 and UBSan trap pass: 56 cases / 792 checks each |
| Supplemental .NET 10 random corpus | 10,000 exact matches; does not replace the failed .NET 9 oracle |
| Freestanding runtime undefined-symbol audit | O0/O2 match baseline: preexisting `__chkstk` only; no new memory intrinsic imports |
| Independent numerical/buffer review | No blocking interval or buffer issue found; allocation trace omission corrected |

The independent Python oracle uses CPython's shortest `repr` digits and exact Decimal tuple manipulation to render the requested exponent thresholds. It also parses each result and compares binary64 bits. It is independent of both the candidate's interval implementation and .NET's formatter. It establishes sampled conformance, not an exhaustive proof over all finite Floats.

The new integration checks cover literal/input/arithmetic results, user calls, locals, record fields, both branch outcomes, one-step conversion accounting, arena and retained-output exhaustion, malformed nonfinite ingress, fuel limits and unpublished failure output. The intentionally unsupported untaken-validator closure test still rejects an unsupported primitive. Full final LLVM/value-stack/mailbox gates have not been run while the contract remains unresolved.

## Native thread-stack limit

The first 256-value corpus body terminated its in-process host with stack overflow. A 32-value batch completed all the same samples at both optimization levels. This changes test shape; it does not fix or prove native stack safety. The exact machine-frame trigger has not been established.

`BackendMetadataPeakBoundBytes` counts explicit emitted LLVM `alloca` storage only. It excludes register spills, machine call/prologue alignment, C helper frames and host interop. It is not a native thread-stack safety bound. Before untrusted native execution, derive and enforce a post-codegen stack budget covering the call graph and audited helper/interop margin. Until then, keep such execution behind a worker-process crash boundary. No untrusted native execution interface or network listener was added here.

## Iteration record and next step

Preserved failures include a test compile error referencing a nonexistent metrics field; a raw fuel fixture that exhausted its 4 KB arena after 1,023 steps; optimizer-generated `memcpy`/`memmove` imports; the fatal 256-value native run; and the exact .NET oracle mismatch. The fuel fixture was enlarged rather than dropping the count assertion. Bounded volatile copy loops remove the new imports. Some first-attempt output was not saved; those observations are retrospective notes, while numbered attempt logs retain later raw failures.

The source candidate and reproducible evidence are retained in the [candidate/evidence archive](evidence/224-native-float-format-findings/candidate-and-validation.zip) and [verified member index](evidence/224-native-float-format-findings/index.json). Do not merge or claim this candidate passes until the formatting contract is resolved, both backends implement it, and fresh regression gates pass. Report 222's lack of a comparative agent reliability advantage remains unchanged.
