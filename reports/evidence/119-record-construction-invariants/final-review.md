# Final read-only review

Reviewer: `record_validation_review`.

## Findings

No blocking semantic, persistence, or test-quality issue was found in the reviewed final deltas.

- The persisted record-validator identity checks cover both Flow and Stack. The Stack test commits the authored source objects, then changes only the stored target in fresh candidate manifests. A missing target is rejected as `TYPE_VALIDATOR_TARGET_MISSING`; a schema-valid forged target is rejected as `TYPE_VALIDATOR_TARGET_MISMATCH`. In-memory Stack staging still begins unbound and binds at commit. Flow commit/reload tests also prove the predicate’s stable `WordId` is retained, the generated constructor remains a dependency, and the implicit predicate call does not become an authored test call binding.
- Business qualification is grounded in attached tests for no match, matching customer, and mismatching customer. The mismatch expects constructor rejection, while coverage records the validator’s completed `false` return. Both match outcomes and both `Bool` returns are required before the predicate reaches library maturity. The extension verifies byte-exact type/word source and test/example reload, stable identity, persistent validator metadata, and rejection after reload.
- Expected-error evidence is bounded correctly: a completed validator return before a later error is retained; a target that throws before returning contributes no fabricated output; failed value and error-code assertions contribute no qualifying invocations. Runtime aggregation includes only passing attached tests.
- Native tests compare valid values and instruction fuel, and compare the full constructor-site rejection diagnostic with the interpreter at O0 and O2. Retained-root reentry reconstructs through the generated constructor using entry bodies from the same verified IR program.
- Finite coverage rejects validated records with finite domains or finite projections, including the zero-field singleton case; an open-only record remains supported without finite obligations. The documented limitation is accurate: the predicate and pure helpers see an unchecked candidate of the nominal record type and must not assume its invariant. Current predicate results are not used as proof or optimization assumptions, and rejected candidates do not escape to ordinary outputs or effects.

One optional test-strengthening item remains: the docs say predicate execution errors propagate, and the interpreter/LLVM paths appear to do so, but the reviewed focused tests do not explicitly use a validator that throws before returning. This is not an observed defect or a blocker.

## Validation evidence

I did not run builds or tests. The implementation owners reported these focused results, and the solution Debug build repair completed with 0 warnings and 0 errors: Business Transitions 9 groups / 5,479 assertions; Flow Runtime 30 groups / 979 assertions; Storage 16 groups / 370 assertions; IR 191 assertions; LLVM 455 assertions. The root agent was starting the full gate when this review was finalized.

## Reviewed source SHA-256

Hashes were read from the shared checkout after the owners reported their final focused runs and freeze. Paths are repository-relative.

| File | SHA-256 |
| --- | --- |
| `src/AgentLang.Core/Compiler.fs` | `22D5F12CA4F68B199314DB04B6F49658516E85765BA989E14C0ACA3960929486` |
| `src/AgentLang.Core/Core.fs` | `57FE2D9512AFA26FC920C06A8B4558BDF965157E27A2BD35CEDA64DFABB85428` |
| `src/AgentLang.Core/Discovery.fs` | `E79746A41666D11ACF928C201E054A4F7CCCC0C2D04DE3C95BD72DB406624842` |
| `src/AgentLang.Core/FiniteCoverage.fs` | `CB3F072280035D004BCD573648E6DA5B1B396C8AB6D0C2760CBF243793695E3C` |
| `src/AgentLang.Core/FlowLowering.fs` | `1D9C6871408B33B9D0A7EFF51EE3F354093409234FC10C1E07F33A1655C1292C` |
| `src/AgentLang.Core/IrInterpreter.fs` | `F223A37384AC014BEF6C48DC920F034ABA530761DCC441C46BBA30F2151AA24A` |
| `src/AgentLang.Core/Runtime.fs` | `F5BAB04A89B8CD6C268E75E0BB9B265112843A81E388920AF0942D9C783079D9` |
| `src/AgentLang.Core/TypedIR.fs` | `2994D472747C01C15B5E2091BD5DBAAD6D07493FF10B781EB867712266FAB013` |
| `src/AgentLang.Core/VocabularyAnalysis.fs` | `0BE80D5BC6F5774151CC054E2C4225297C4BBFCB8D09D5F91B447F4D295BD826` |
| `src/AgentLang.Llvm/LlvmAot.fs` | `1A713D87C216EC0F0E41EF13072AE102797F5A612352C8D82E861BE3E871FFE0` |
| `tests/AgentLang.Business.Transitions.Tests/Program.fs` | `8DC1E478CD1A20D18D2E3C7E057BB4777E6967E967DCDD2B3F3DCFFD484F18EC` |
| `tests/AgentLang.Flow.Runtime.Tests/Program.fs` | `889921B253589C572167BDA767A3CEDDC163A440D05D796A78B000958C44D8BF` |
| `tests/AgentLang.IR.Tests/Program.fs` | `49D0DF0650F4AE2F13303778F3D7CB4F4B73198D23BDB70E1F5E67C54D3226A7` |
| `tests/AgentLang.Llvm.Tests/Program.fs` | `077C6520E7ED4FAABF21B618CDFA118EE088AAB447C962034DC3BAF538B12029` |
| `tests/AgentLang.Storage.Tests/Program.fs` | `2529AC34C12FF635E3BB2F7FEEBBA2761EF0557393E3A5538B13057443A0B17D` |
| `examples/business-validated-lookup.agent` | `374CD3C2B9E8EE6FA2DC6BD946CAFAE8B368ACDC295B9154781A4843BF28E6E8` |
| `docs/RECORD-VALIDATION.md` | `60FEF42D0DFF1036323385AB3DF7644283A027CC9FEE7C1133ACA5AFFEC11300` |
| `docs/FINITE-COVERAGE.md` | `44F60A2515E4B6372DAEEE816EE2F966A17EBDF48043CE76E256AA6D59F37E98` |
