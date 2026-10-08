# Validated basis-point Money scaling

Added a Flow/2 scalar UnitBasisPoints over Int with an inclusive 0–10,000 validator. The total scaler returns Money directly and uses only existing checked source-language integer operations:

1. q = amount / 10,000
2. r = amount - q * 10,000
3. result = q * rate + (r * rate) / 10,000

With 0 <= rate <= 10,000, |q * rate| <= |q * 10,000| <= |amount|; |r| <= 9,999, so |r * rate| <= 99,990,000. Both terms have the amount's sign (or are zero), and the final magnitude cannot exceed the input amount. This remains safe at Int64.MinValue and Int64.MaxValue and truncates toward zero. Totality applies to UnitBasisPoints values validated through the language constructor; this does not claim that an arbitrary forged host/native nominal payload is validated. No new primitive was added.

The fixed caller money.scale-by-90-percent supplies UnitBasisPoints::new(9000). Its parsed signature is Money -> Money, with no Result output or synthetic error branch. Its attached test passes; the word is committed at library maturity and its own coverage has zero uncovered instructions and branch outcomes. The generic Result-based ratio wrapper and its nine existing authored tests remain in the fixture.

The Business.Language property check uses an independent BigInteger oracle for 211 cases: 77 amount/rate edge combinations, 128 deterministic generated combinations, and 6 explicit cases. Authored Flow/2 tests cover the validator at 0, 10,000, -1 and 10,001; constructor rejection at -1, 10,001, Int64.MinValue and Int64.MaxValue; zero/full rates at Int64 extremes; positive/negative truncation; and the fixed 9000 caller. All 27 authored Flow/2 cases pass before commit and after fresh reload.

Runtime coverage is session-local and describe uses the most recent test batch. The test harness therefore inspects each word immediately after running that word's attached tests. A fresh Engine reports not-run before its own tests execute; after reload, the harness reruns the authored cases and checks per-word coverage again. Library status and the UnitBasisPoints source persist across reload.

## Validation

Command, run from D:\code\AgentLang\.agentlang\structural-correctness-001:

    dotnet run --project 'D:\code\AgentLang\tests\AgentLang.Business.Language.Tests\AgentLang.Business.Language.Tests.fsproj' --configuration Debug --artifacts-path 'D:\code\AgentLang\.agentlang\structural-correctness-001\artifacts-ratio-basispoints-r2'

Result: 7 groups, 6,319 assertions; 110 attached tests; 27 examples; 36 committed words; 27 types. Final output is in logs/business-language-bp3.log. git diff --check passed. Debug artifacts stay under .agentlang/structural-correctness-001; canonical Release artifacts were not used.

Earlier focused attempts are preserved. business-language-bp1-failed.log records the initial mismatch between the scalar Money result and a helper that expected Result<Money,...>. business-language-bp2-failed.log records the transient coverage query after a fresh reload; business-language-bp2-coverage-cache-failed.log records that same failure. The successful run uses a scalar output helper and checks each word immediately after its own test batch.
