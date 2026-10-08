# Checked ratio primitive implementation summary

Implemented int.scale-ratio-toward-zero as a pure typed primitive with signature Int Int Int -> Result<Int, String>. It accepts the full signed Int64 range for all three inputs, multiplies exactly in Int128, divides with signed truncation toward zero, and checks the final quotient against Int64 bounds. A zero denominator returns DIVIDE_BY_ZERO; an out-of-range final quotient returns INT_OVERFLOW. Exact products larger than Int64 are allowed when division brings the result back into range.

The compiler primitive catalog and interpreter dispatch preserve the existing verified-call IR path. The primitive takes positional inputs; the catalog has no argument-name metadata. The authored Flow/2 wrapper has typed parameter names and a nominal Money result, and named/reordered wrapper calls pass before and after reload. The original examples/business-values.agent fixture remains unchanged at 32 words and 83 tests. Its separate Flow/1 baseline is loaded and committed first; examples/money-ratio.agent is then explicitly defined with syntaxVersion = 2, tested, committed, and loaded by a fresh Engine.

The primitive's independent BigInteger oracle covers 13^3 = 2,197 edge tuples and 512 deterministic generated tuples, plus 10 explicit direct cases (2,719 direct oracle/property evaluations total). The Money wrapper oracle covers 11^3 = 1,331 edge tuples, 64 deterministic generated tuples, and 6 explicit cases (1,401 wrapper evaluations). Nine authored Flow/2 cases run before commit and after fresh reload.

The LLVM scalar backend does not support Result types. Its focused test verifies deterministic rejection at the current type boundary: IR_LLVM_UNSUPPORTED_TYPE, Actual = ["Result<Int, String>"], at the ratio call span. This does not claim native ratio support or exercise a primitive-whitelist rejection. Existing nominal Int and Bool LLVM behavior remains covered by the passing LLVM suite.

## Focused Debug validation

- Trusted values: 8 groups, 3,206 assertions — logs/trusted-values.log.
- Flow: 1,051 assertions — logs/flow.log.
- Business.Language: 7 groups, 5,957 assertions, 92 attached tests, 27 examples, 33 committed words and 26 types — logs/business-language-r3.log. The 33 words include the one separately committed Flow/2 extension word; the frozen base fixture remains at 32.
- LLVM: 442 assertions, including the unsupported type boundary — logs/llvm.log.
- git diff --check passed after the focused Business.Language run.

Artifacts use Debug builds beneath .agentlang/structural-correctness-001. Earlier failed focused Business.Language attempts are retained as logs/business-language-r2-failed.log and logs/business-language-r2.log; the final passing attempt is logs/business-language-r3.log.
