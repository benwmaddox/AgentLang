# Checked ratio independent review

Read-only source review; no builds, tests, actor contact or production edits. Reviewed the primitive, compiler/IR integration, Money wrapper and changed tests at the hashes below. Files may still change during focused validation.

No correctness blocker found. Int64 operands have magnitude at most 2^63, so their exact product has magnitude at most 2^126 and fits signed Int128. Division by a nonzero Int64 cannot produce the Int128 minimum/-1 overflow case. The implementation checks zero denominator first, computes the exact product before division, uses signed truncation toward zero, then checks only the final quotient against Int64 bounds. Thus a wide intermediate may yield a valid final value; zero denominator remains DIVIDE_BY_ZERO even with zero numerator; final out-of-range quotient returns INT_OVERFLOW as a value error.

Compiler primitive signature is Int Int Int -> Result<Int,String>, with pure effects; interpreter registry and RuntimeResult mapping agree. The Money wrapper explicitly unwraps minor units, preserves Money on success and forwards the same String error without converting it into a business error record. Tests cover runtime typed errors and wrong operand rejection. Native support is explicitly absent. LLVM type preflight rejects Result<Int, String> before the primitive whitelist, even when the Result is subsequently dropped. The corrected test asserts IR_LLVM_UNSUPPORTED_TYPE, the ratio source span and Actual = [ "Result<Int, String>" ]. It does not demonstrate a primitive-whitelist diagnostic. The earlier review incorrectly attributed rejection to that whitelist; this paragraph corrects that source-review conclusion.

Validation design is meaningful: an independent BigInteger oracle drives signed edge Cartesian products and deterministic generated cases, including zero denominators, signed extremes and wide products. The wrapper now resides in separate examples/money-ratio.agent, parsed and defined explicitly as Flow/2 after the original business library is committed and reopened. Original examples/business-values.agent is restored, with no source diff and Git blob 8dc9c4596b9fe88860ae5c243698b20d7b71c682 matching HEAD. The base retains 32 words/83 attached tests; the separate one-word/nine-test extension brings the combined totals to 33 words/92 tests. The business runtime group runs 1401 independent BigInteger cases before commit (11 cubed edges, 64 generated cases and six explicit cases), checking Money on success and exact String errors. After commit, a fresh Engine checks identical authored fn source, all nine attached test names/results, and a reordered named-argument call preserving the Money result. It does not rerun the full 1401-case property suite after reload. Current Flow/2 tests parse and lower a fn wrapper with typed Result<Money,String> and execute its successful branch; direct primitive error tests plus business wrapper error tests cover error semantics in other integration paths. These are finite properties plus a bounded arithmetic argument, not exhaustive enumeration of every input tuple.

The earlier misleading explicit overflow-test label is corrected: MinValue * -1 / 1 is now described as above MaxValue when MinValue is negated. Negative overflow remains covered by the Cartesian edge tests.

Frozen research Release artifacts were not built or modified by this review. Artifact preservation verification remains the main agent's validation responsibility.

## Reviewed SHA-256 hashes

- src/AgentLang.Core/TrustedValues.fs: c26edc9681c879c5f0a410274901d6e1cfc3c515f7628f139235747daad57659
- src/AgentLang.Core/Compiler.fs: 3f02e6361c1c7600fdffe41970cadf5d649cc16c46ce285312b1b62e3f9bc639
- src/AgentLang.Core/IrInterpreter.fs: 1a3fb8c2b548200c31b8744e3e9e46951c6a9467f805660131df87eff2009184
- examples/business-values.agent: fe8227ce0c38ff6e193a74292a5b1d0ed0ac3e3869f0fe8feaf56eca62f30cbd
- examples/money-ratio.agent: 3efb292475f00b78eb71449b33eca000c7d5613e041f9c4115c65627bc0e5b9c
- tests/AgentLang.TrustedValues.Tests/Program.fs: c4c3809edcb3f3008a86fe54b8bca063122d11ca3fe3d7cf5aa93aa5ca56e2b4
- tests/AgentLang.Flow.Tests/Program.fs: af2003b5331f1539bcb93495c8258dd9464078d6aebc7b4f7c956b6c5d8554af
- tests/AgentLang.Business.Language.Tests/Program.fs: 0c631dec6d816e5419ae85d848acae49aa9a4510d361008683f914197c0132d7
- tests/AgentLang.Llvm.Tests/Program.fs: c34418ac130ed3e87d356e8993c681be61d9d032ae417d5d2faa595f3fa2b0fa
