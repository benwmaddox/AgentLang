# Structural correctness: explicit arithmetic and domain contracts

Delivered: the first arithmetic slice of the approved structural correctness
requirements, with local regression and property checks. Closed domain states,
complete-record construction invariants and additional stateful properties remain
pending. The prepared stateful comparison has not launched actors; this report is
implementation evidence, not an agent-efficacy result.

## Behavior

The new pure trusted operation `int.scale-ratio-toward-zero` takes three Ints and
returns `Result<Int, String>`. It multiplies exactly in Int128 before signed
division, truncating the final quotient toward zero. A zero denominator returns
`DIVIDE_BY_ZERO`; a final quotient outside Int64 returns `INT_OVERFLOW`. A product
larger than Int64 is allowed when the final quotient fits. No Float conversion or
premature rounding occurs. Compiler metadata and interpreter execution use the
existing verified primitive-call IR.

The separate Flow/2 library in [money-ratio.agent](../examples/money-ratio.agent)
contains four authored functions and one validated type:

- `unit-basis-points.valid?` accepts exactly 0 through 10,000.
- `UnitBasisPoints` enforces that predicate through its generated constructor.
- `money.scale-basis-points-toward-zero` returns Money directly for a valid rate.
- `money.scale-by-90-percent` composes that total function with a rate of 9,000.
- `money.scale-ratio-toward-zero` wraps the general checked operation and retains
  its potentially failing Result contract.

All four functions qualify under the current library instruction/branch gate.
The new library adds 27 attached tests. Existing business fixture sources and
expected counts remain unchanged; the extension is loaded separately after the
original library is committed and reopened.

## Why two arithmetic contracts

An arbitrary ratio can fail. A fixed 90% multiplier cannot overflow any valid
Int64 amount, so matching a generic Result would require an unreachable error
arm and conflict with complete own-branch coverage. The total bounded-rate
contract removes that mismatch without weakening coverage or fabricating errors.

For amount `a`, denominator `d = 10000`, and validated rate `b`, the library uses
`q = a / d`, `r = a - q*d`, then `q*b + (r*b)/d`. Every intermediate fits Int64:
`q*b` is bounded by `q*d`, the remainder product has magnitude at most 99,990,000,
and both terms have the amount's sign. Their sum is exactly the desired truncated
quotient and has magnitude no greater than the amount, including Int64.MinValue.
This total function uses existing primitives; it adds no trusted host operation.

The totality claim applies to valid values produced under the reviewed type
contract. Generated constructors enforce the bounds, including after reload.
This work does not establish validation of forged foreign/native ABI payloads.
The general Result-returning ratio operation remains unsupported by LLVM: its
conformance test checks explicit `IR_LLVM_UNSUPPORTED_TYPE` rejection at the call.
No new native arithmetic support or native performance result is claimed.

## Validation

The dirty working tree based on `91fe813984c4fb4f5834bc4c61ba2bbb81ff8ec2` passed:

| Local runner | Result |
| --- | --- |
| TrustedValues Debug | 8 groups, 3,206 assertions |
| Flow Debug | 1,051 assertions |
| LLVM Debug | 442 assertions |
| Final Business.Language Debug | 7 groups, 6,319 assertions |
| Full Debug regression | 37/37 checks |
| Business-policy preflight within full gate | 98 checks, 30 independent outcomes |

The full gate ran with `pwsh -NoProfile -File scripts/Validate.ps1 -Configuration
Debug -ReportPath .agentlang/structural-correctness-001/full-validation-r2.json`.
It finished before the final bounded-rate test edits. A fresh focused Debug build
and Business.Language run covers those last two changed files; no runtime source
changed after the full gate's build. The final focused command and detailed counts are saved
in the [evidence](evidence/110-structural-correctness/index.json) summaries.

Independent BigInteger expectations cover 2,719 direct generic-ratio evaluations,
1,401 Money-wrapper evaluations, and 211 bounded-rate evaluations (77 edge pairs,
128 deterministic generated pairs, six explicit pairs). These properties run
before commit. After reload, tests verify exact function/type source, invalid
constructor rejection, all 27 attached cases and the fixed-rate caller. The full
property sets are not rerun after reload. Combined with the unchanged foundation,
the final runner exercises 110 attached tests, 27 examples, 36 words and 27 types.

Coverage observations are session-local and expose the latest test batch. Fresh
reload initially reports `not-run`; the tests check each function's complete own
coverage immediately after its own test command. Library maturity and definitions
persist independently. This milestone does not implement the pending general
finite-input/return-value qualification rules.

## Corrections and review

The first full gate failed two historical fixture checks because the initial
wrapper was added to the baseline business file. The fix restores that file and
loads the new extension separately; frozen expected counts were not changed.
The final full gate passes. Focused test-harness fixes corrected response-shape
assumptions, a Result helper incorrectly used for a total Money value, and an
assumption that coverage accumulated across separate test commands. Saved logs
include failed attempts as well as the final passing runs.

Independent source review checked exact arithmetic, nominal construction paths,
coverage assertions and persistence scope. All ten frozen Release runtime hashes
still match report 108. Validation used Debug artifacts. No CI runs were enabled.
Readable evidence text is normalized to LF; the full policy JSON is preserved
in a ZIP. All evidence is indexed by SHA-256. Source hashes in review
notes identify the reviewed working-tree bytes.

## Remaining implementation and research

The [approved requirements](../docs/STRUCTURAL-CORRECTNESS.md) also call for closed
internal states with exhaustive decisions, invariant-preserving construction,
pure decisions with narrow effects, and independent stateful properties.
Validated status strings are not closed enums. Public generated record
constructors still bypass cross-field rules enforced only by optional helpers.

Next: payload-free closed enums in a new fixture, verified exhaustive matching,
durable source and truthful finite-coverage qualification; then complete-record
construction invariants and decision/executor properties. Preserve earlier
research fixtures. Arithmetic checks do not establish idempotency, external
state preservation or improved agent reliability. The next external-agent study
must test those benefits rather than infer them from passing implementation tests.
