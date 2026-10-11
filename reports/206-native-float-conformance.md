# 206 — Native Float conformance

Status: accepted for the current owning AOT backend.

Finite binary64 Float values now work through literals, direct inputs/outputs,
locals, calls, branches, records, Option/Result payloads, nominal wrappers and
frozen pure refinements. Arithmetic, ordered comparisons, numeric equality,
Int conversions and ties-away-from-zero rounding match the authoritative typed
IR interpreter. Signed zero and subnormal bits are preserved. Typed aggregate
equality compares Float values numerically, so opposite signed zeros compare
equal inside both fixed and dynamic records/sums.

Non-finite inputs and malformed owners are rejected before unsafe reads or
publication. Arithmetic overflow, signed-zero division and conversion range
failures preserve complete structured diagnostics, instruction fuel and atomic
output behavior. Rounding uses exact significand/remainder operations with
bounded shifts; it does not add 0.5 or depend on a platform rounding routine.
No new external-effect capability or .NET escape hatch is introduced.

The coordinated contracts are owning stack ABI 2, layout ABI/schema 4, mailbox
module ABI 2 and F64 descriptor kind 9. Context size is 176 bytes, with
`diagnostic_float_bits` at offset 168. Existing primitive/nominal/String IDs
remain stable where specified; compound IDs can shift. The separate legacy
shared-graph ABI 3 and generic module ABI remain unchanged. Prototype artifacts
must be rebuilt together after this breaking contract change.

Validation on frozen sources, all locally:

| Gate | Result |
| --- | --- |
| Final serial Release solution build, package audit explicitly disabled | 0 warnings/errors |
| IR interpreter tests from the built solution | 155 assertions |
| Focused owning Float suite from fresh dependencies | 176 assertions |
| Complete LLVM suite from the same fresh build | 950 assertions |
| Native value-stack verifier | 50 checks; source hashes stable; 44 arena-lifetime assertions |
| C value-stack suite at O0, O2 and UBSan trap mode | 54 cases / 751 checks per run; fixed 16/189, dynamic 38/562 |
| Owning mailbox integration, serial fresh build | Passing main and refined reports: 2,255 and 668 checks respectively; overlapping reports, not an additive total |
| Direct owning mailbox, runtime, bank and release C gates | Passing recorded O0/O2 runs, plus mailbox trap mode |
| Independent source reviews and final whitespace check | Passing scoped reviews; all final source pins match |

The focused suite tests exact signed-zero/subnormal/max-finite bytes, conversion
edges and adjacent half-integers, complete failure diagnostics, nominal identity
and refinements, fixed/dynamic equality, malformed finite/owner inputs, and real
mailbox initialize/begin/resume under RETURN and KEEP_ASSOCIATED at O0/O2.
Raw callback tests also cover diagnostic and trusted-generated profile contracts.
The actual controller helper links normal Windows CRT/SDK libraries; the
production generated module keeps its existing minimal link.

The full LLVM suite is invoked from the fresh serial Release/audit-disabled
build rather than repeating the default build wrapper's known environment
limitations. This executes the complete suite used by
`scripts/Verify-NativeConformance.ps1`. The value-stack and mailbox verifier
scripts themselves were rerun. Normal UBSan runtime linking is unavailable in
this installation; trap mode passes. No package was added and CI remains manual.

The [evidence archive](evidence/206-native-float-conformance/validation.zip) and
[member index](evidence/206-native-float-conformance/validation.index.json)
retain final source snapshots/pins, original failures, successful logs/receipts,
static oracles, source reviews and iteration notes. Root reopens and verifies
all archived member lengths and SHA256 hashes before publishing the milestone.

Earlier attempts are preserved. They found source interpolation/exhaustiveness
errors, test compilation mistakes, a missing Windows rounding symbol, incorrect
harness payload/count/extent/profile/source-span assumptions, and an incomplete
lifecycle-helper link. The runtime rounding dependency was removed; harness
repairs preserve the strict assertions and add malformed-input controls. The
first mailbox gate found a stale fixture byte pin after the planned ABI update;
its value/step/copy oracles and correction history remain unchanged. The first
value-stack gate found a stale 44-case predicate after removing the old Float
rejection case (43 cases / 86 named checks remain); exact evidence-count checking
was added. A second value-stack run is explicitly failed because sources changed
while it ran; only the final stable run is accepted. An initial C temporary-file
rename failure is recorded retrospectively because its raw log was not saved.
Canonical LF source bytes were frozen before the final fresh build and gates.

Limits: native `float.to-string` remains explicitly unsupported; formatting must
not be approximated with platform `%g`. Legacy graph Float support, general
release coverage and JIT remain follow-ons. The native ABI assumes readable
compiler-generated descriptor pointers; these bounded owner/layout checks are
not an OS sandbox. This milestone establishes conformance, not final memory
policy, production readiness or a throughput advantage.

Agent efficacy remains at [report 203](203-efficacy-decision-after-organic-creation.md):
bounded discovery/composition is observed; comparative reliability and benefit
from organic vocabulary accumulation remain unproven. This is progress on the
secondary native-backend track and does not alter the closed agent trials.
