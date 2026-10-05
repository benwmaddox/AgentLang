# Flow-authored cases integration

Status: standalone Core Flow tests/examples implemented; focused checks and all 24 local Release checks passed. Committed CI/publication is tracked below. Durable Flow Runtime integration remains unfinished.

## Plan and acceptance

Implement the Core surface in [FLOW-ATTACHMENTS.md](../docs/FLOW-ATTACHMENTS.md): versioned authored test/example objects, standalone parsing and deterministic rendering, source-aware lowering, and compilation against the exact verified program. Preserve literal, runtime-error-code and pure expected-expression tests; examples remain literal-only. Keep complete output-vector semantics and lexical block returns.

One lowering state must allocate disjoint markers for actual and expected bodies, seeded from retained context origins. Shared structural preflight must budget both bodies before recursive consumers. Diagnostics must refer to authored spans. Actual test execution may cover only the target library word's own sites while that word is active; inline fixture branches and isolated expected-expression branches must not satisfy its obligations.

The source worker owns FlowSyntax, FlowParser, FlowLowering, focused Flow tests, attachment/syntax/surface documentation and report 031. Root owns this integration report, requirements, serial validation and publication. A separate read-only audit cross-checks the existing durable integration plan against current storage/runtime APIs. The canonical prototype checkout is shared with non-overlapping ownership; no worktree is used.

## Validation sequence

After source freeze, rebuild and run the focused Flow suite, review source mapping and coverage isolation independently, then run the IR/interpreter checks and full fresh Release gate:

```powershell
pwsh -NoProfile -File scripts/Validate.ps1 -Configuration Release -ReportPath .agentlang/reports/032-flow-cases-validation.json
```

Preserve failed attempts and raw terminal evidence; update reports with actual results before committing/pushing. Runtime/protocol/storage behavior is not changed by this Core milestone. Fresh-process authored Flow persistence, revision hashes, complete project lowering, exact-root addressing, rollback and default cutover remain later required gates.

## Publication dependency

At the start of this continuation, output-vector commit `f5d59f349c98053560a30e38bd94cd3152f49332` is pushed to prototype but not merged. GitHub run 37368820428 remains queued with no workflow steps executed. GitHub confirms the repository is private. Continue local implementation while that specific run waits; do not infer a language validation failure from runner acquisition delays.

## Remaining full goal

The complete PRD remains active. This milestone does not prove agent productivity, controlled A/B/C benchmark outcomes, native memory benefits or completion of the runtime/frontend migration. Actual subagent experiments follow the complete default Flow cutover, with persistent vocabulary and conventional controls.

## Durable boundary audit

The read-only storage/runtime audit confirmed that the current format-version constant is shared by pointers, manifests and snapshots; revision parsing has no schema context. The plan now requires separate version handling and v1 serialization that omits v2-only fields. Current tests generate v1 manifests dynamically, so they do not prove byte/hash compatibility. A bounded test worker is adding literal golden v1 objects and pinned hashes without changing production Storage APIs.

The audit also identified an unspecified durable binding location. The authoritative integration plan now specifies ordered per-revision `callBindings` entries scoped to the exact authored source reference, using structural authored AST site keys and stable target IDs. Aggregate ProjectSource becomes a byte-validated export projection for v2; manifest revision metadata selects parsers directly. These are future integration contracts, not implemented v2 behavior.

## Early Core compilation

The early `dotnet build src/AgentLang.Core -c Release` failed with four `FS0001` record-label inference collisions in FlowLowering (lines 775, 882, 916 and 921). It reported zero warnings and four errors. Saved the [diagnostic transcription](evidence/032-early-core-build-failure.json). The source owner is adding explicit type annotations before a serialized rerun. This build was taken before focused tests and source freeze; it proves no execution result.

The second early Core build retained one `FS0001` inference error in `compileExpression`; its [raw output](evidence/032-early-core-build-second-failure.json) is saved. Explicit return types on compiled/lowered wrappers resolved the overlapping record-label ambiguity. The third Core Release build passed with zero warnings and zero errors. Core is frozen for independent semantic review while the focused attachment fixtures are still being authored. This is compilation evidence only.

## Frozen v1 compatibility fixture

The bounded storage prerequisite adds literal CURRENT, manifest and four authored source objects under `tests/AgentLang.Storage.Tests/fixtures/v1-golden/`, with six pinned SHA-256 values. The new test materializes those bytes without invoking the production serializer, reads the stored revision, and exercises named snapshot restore plus task-style capture/restore after intervening commits. It checks that the original manifest identity, source objects and export bytes remain intact while generation advances. Repository LF checkout policy preserves the fixture encoding.

The worker's focused `dotnet run --project tests/AgentLang.Storage.Tests -c Release` passed with 9 groups / 105 assertions, exit code 0. Root reviewed the test and fixture ownership; no production Storage schema/API was changed. The forthcoming full Release gate will independently include this suite. V2 migration and Runtime Flow persistence remain unimplemented.

## Focused review before execution

Root and independent review identified a disconnected fixture-coverage assertion: it checked a collection that the fixture host never updated. The owner routed fixture and actual traces through the same target-owned-site filter, and changed the pure expected-expression test to execute the tested library's opposite branch with a separate trace. Added acceptance also requires conservative unselected-branch effects, nominal Email versus String expectations, and sparse retained-marker allocation. Focused execution is still pending; neither interim source inspection nor an empty coverage collection is a passing coverage result.

Output-vector CI attempt 2 also terminated before acquiring a hosted runner, with no steps run. Its evidence is saved in report 030. No third immediate retry was requested. Publication of the combined validated source milestone will trigger a new exact-commit run; merge remains dependent on committed validation.

## First focused Flow attempt

`dotnet run --project tests/AgentLang.Flow.Tests -c Release` stopped during test compilation: `FS0001` at Program.fs line 1493 compared the literal-only example expectation with the test-expectation union, and `FS0072` at lines 1568/1571 required collection type annotations in the coverage helper. Saved [raw failed output](evidence/032-focused-flow-build-failure.json). The owner is repairing test fixtures; frozen Core compilation remains clean. No focused execution pass is claimed yet.

The next focused run built successfully but failed the sparse-fixture precondition: its retained word had only one synthetic marker, so there was no marker pair to test. After adding a second real retained Flow word, a subsequent run failed the attachment-allocation assertion because the chosen simple direct calls allocated no synthetic attachment sites. Saved [retained-marker fixture failure](evidence/032-focused-flow-sparse-fixture-failure.json) and [attachment-marker fixture failure](evidence/032-focused-flow-marker-fixture-failure.json). The fixture is being changed to authored conditional expressions that require real scope markers on both actual and expected sides. No production guard or allocator behavior is being relaxed to satisfy the fixtures.

## Focused Flow pass

After making the marker fixtures non-vacuous with real conditional scopes, the focused Release run passed 354 assertions, exit code 0. Saved [terminal output](evidence/032-focused-flow-success.json). The suite covers all expectation kinds, literal examples, exact authored bytes, closed nominal Email expectations, nested constructors/matches/destructuring, shared structural limits, sparse retained markers with a recompiled exact snapshot, conservative effects, and exact own-site/outcome coverage pairs with isolated expected calls. Core/test source is frozen for final independent review and the complete Release gate. Runtime/durable migration remains separate.

## Final local integration

The complete fresh Release gate finished with exit code 0 and all 24 checks passing. The solution build had zero warnings/errors. Flow passed 354 assertions, lint 66, storage 9 groups / 105 assertions, IR 102, interpreter 22, formatting 39, and language acceptance 34 groups / 583 assertions. Source/value/harness/business/contracts/conventional/discovery/vocabulary checks passed, along with fresh-process persistence, 70 matched fixture checks, 17 trial-host checks, 5 parser-limit checks and both whitespace checks.

Saved [full local report](evidence/032-flow-cases-validation.json) plus projection, matched-fixture, trial-host and parser reports beside it. These reports identify the dirty prototype checkout based on `f5d59f349c98053560a30e38bd94cd3152f49332`; they are local evidence, not exact-commit CI proof. The task bank still validates artifact shape (2,114 assertions / 60 tasks / 180 proposed vectors), not execution of the benchmark tasks. No controlled agent or native-memory benefit is claimed.

The implemented scope keeps Flow authored source/version/span metadata alongside derived Core/verified IR; actual and expected lowerings share a seeded marker allocator and shared structural budget. Tests are scalar assertions with strict nominal equality and pure expression expectations. Exact own-site branch/outcome obligations stay isolated from fixture and expected-expression traces. Frozen v1 storage fixtures now pin compatibility bytes without changing production schema. Complete batch lowering, exact root qualification, v2 authored source/history/bindings, Runtime rollback/default cutover, providers/domain and controlled external-subagent experiments remain required.

Final independent read-only review found no material issue in frozen Flow syntax/parser/lowering or attachment tests. It confirmed that sparse retained markers are backed by remapped word bodies and matching origins, and coverage assertions compare exact target-owned site/outcome pairs with expected execution isolated. Final refinement retained existing Core/IR contracts and the explicit standalone-versus-Runtime boundary; all working whitespace checks pass.

## Exact committed local validation

After pushing `1e3aba27d0fb1db2a7f3670726e126b806ba6b0f`, root rebuilt the entire Release solution with `--no-incremental` (zero warnings/errors), then ran the exact workflow validation command locally: `pwsh -NoProfile -File scripts/Validate.ps1`. All 24 checks passed, exit code 0. The [committed validation report](evidence/032-committed-validation.json) identifies that exact SHA, branch prototype and `dirty: false`; adjacent committed projection, matched-fixture, host and parser reports are saved. This provides fresh exact-commit local Windows evidence, not a successful GitHub Actions run.

GitHub confirms the repository is private before and after the source push. [CI 37374035105](https://github.com/benwmaddox/AgentLang/actions/runs/37374035105) is queued for the same SHA with no steps executed at this observation. Previous output-vector runs failed solely to acquire a hosted runner; merge/publication status must distinguish that hosting state from local validation. The next root-addressing implementation proceeds independently.

## Successful committed CI

[GitHub CI 37374035105](https://github.com/benwmaddox/AgentLang/actions/runs/37374035105) acquired a runner and passed for exact source commit `1e3aba27d0fb1db2a7f3670726e126b806ba6b0f`. Downloaded and inspected the uploaded [validation report](evidence/032-ci-validation.json): exact revision, `dirty: false`, `passed: true`, all 24 checks exit 0. Saved [run metadata](evidence/032-ci-run.json) and adjacent CI projection, matched-fixture, host and parser reports. This supersedes the pending committed-validation boundary for the combined output-vector and authored-case milestone; earlier runner-acquisition failures remain recorded accurately in report 030.

Publication uses a reports-only commit above that verified source commit. Before fast-forwarding main, require an empty executable/test/workflow diff from the tested SHA, current remote main ancestry, private visibility, and clean staged whitespace. Keep in-progress root-addressing source outside that publication commit. Report 031's header example and failed-fixture description were corrected against the saved actual syntax and failure outputs; no executable behavior changed in these corrections.
