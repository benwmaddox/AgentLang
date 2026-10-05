# Flow root addressing integration

Status: exact-root Flow calls/callbacks implemented; focused checks and all 24 local Release checks passed. Committed publication is tracked below. Durable/default frontend cutover remains unfinished.

## Plan and acceptance

Close the exact-root naming gap before durable Flow source is frozen. Add a distinct RootCall with root name and target span, preserving existing suffix/namespace Call semantics. Callback references use a closed ExplicitShort/NamespaceQualified/AbsoluteRoot qualification union. `::identity(value)` and `items.map(::identity)` select only the exact root dictionary entry; they never fall back to suffix lookup, generated aliases, output type or dictionary order. Bare calls and explicit short callbacks remain ambiguous when namespace suffixes collide.

Validate source and host-built root/reference shapes before recursive consumers. Keep the leading marker in rendering, authored target/full-call spans, named argument binding/evaluation order, effects, strict nominal input/output checking, scalar/vector boundaries and verified IR semantics. Lint traverses root-call arguments, and Flow test/example bodies/expectations round-trip and lower root calls through the same source-aware APIs.

The worker owns Flow syntax/parser/lowering/lint, focused Flow/lint suites, relevant frontend documentation and report 033. Root owns requirements, this report, serial builds/full validation and publication evidence. All agents share the canonical prototype checkout with non-overlapping file ownership and no worktree.

After source freeze, run focused Release Flow/lint checks, independent review, and the complete fresh Release gate:

```powershell
pwsh -NoProfile -File scripts/Validate.ps1 -Configuration Release -ReportPath .agentlang/reports/034-flow-root-validation.json
```

Baseline is Flow 354 assertions, lint 66 and all 24 checks passing on exact committed `1e3aba27d0fb1db2a7f3670726e126b806ba6b0f`. Preserve any failures and update reports before committing/pushing.

## Remaining full goal

Root addressing is one prerequisite. Complete batch signature catalogs/lowering/call bindings, durable v2 authored source/history/identity integration, Runtime/protocol default Flow authoring, library gates across fresh loads/rollback/snapshots, providers/domain and controlled external-subagent experiments remain required. Memory policies and conditional LLVM backends remain research/later work. This slice neither completes the PRD nor establishes agent productivity or native memory benefits.

## Next complete-snapshot inference audit

A focused read-only Compiler/FlowLowering investigation confirmed that call and static-callback inference consume callee signatures, builtin kind and declared effects, without inspecting callee bodies. Flow resolution additionally needs constructor kind and parameter names. Final definition checking consumes each real body; scalar-validator checking also reads validator bodies/dependencies. This supports a type-distinct Flow signature catalog for forward references, with all real bodies assembled before the existing complete-program compiler checks/verifies them. No executable placeholder WordEntry or separate interpreter is needed.

Prefer extending Flow's existing inference/resolution context for this future batch slice, deferring legacy Compiler definition/validator checks to one final complete compile. Catalog validation must preserve primitive-only polymorphism, closed proposed types, nominal identities, known effects, ordered vectors, parameter names and exact stable-ID coverage. Actual/expected attachment compilation remains against the resulting final verified snapshot. This is planning evidence for the next prerequisite, not an implemented batch API. The current code change remains exact-root addressing.

## Parent milestone publication

Committed CI for source `1e3aba27d0fb1db2a7f3670726e126b806ba6b0f` passed all 24 checks. Reports-only publication commit `b04a9d6fd75ddd027ab78cd1d76f1796c51119ad` saved exact local/CI evidence and report corrections; an executable/test/workflow diff against the tested source SHA was empty. After refreshing remote refs and verifying main ancestry/private visibility, root fast-forwarded both remote branches and local main to that publication commit. In-progress root-addressing edits remained intact on prototype. No post-publication main CI result is claimed yet; that new run is separate from the successful source validation.

## Early Core build

The early Core Release build failed before execution: FlowLowering.fs line 282 reported `FS0001` (string versus a three-string tuple), and line 441 reported `FS0039` for the missing `selectRootCallOutputs` helper. Zero warnings, two errors. Saved [raw output](evidence/034-early-core-build-failure.json). The owner is repairing these implementation errors before refreezing Core; no root-addressing pass is claimed.

The repaired Core Release build passed with zero warnings/errors. Core remains frozen while focused root-addressing fixtures are completed. Post-publication [main CI 37375389745](https://github.com/benwmaddox/AgentLang/actions/runs/37375389745) also passed for exact publication commit `b04a9d6fd75ddd027ab78cd1d76f1796c51119ad`; saved [run identity](evidence/034-parent-main-ci.json). This verifies the parent milestone's post-merge gate, not the uncommitted root-addressing implementation.

## First focused Flow attempt

The first focused Flow run stopped at test compilation: the new IR-target inspection helper mixed `VerifiedIrBody` with its inspected `IrExecutableBody`, causing `FS0039`/`FS0001`; two parser-rejection assertions also produced `FS0020` ignored-result warnings. Saved [raw output](evidence/034-focused-flow-build-failure.json). The worker is repairing the fixtures and adding direct root-qualified callback input/arity/output-vector rejection cases requested by review. Core remains frozen; no focused execution pass is claimed from this attempt.

## Focused execution and final effect fixture

After correcting the IR inspection helper and ignored diagnostics, focused Flow passed 406 assertions and lint passed 68, both exit 0. Saved [initial Flow pass](evidence/034-focused-flow-initial-pass.json) and [lint pass](evidence/034-focused-lint-pass.json). Direct root callback input/type/arity/vector/result checks, exact IR targets, named argument order, attachment source/projection and non-vacuous lint local reads are covered. Review requested one further effect-boundary fixture: denied root-callback preflight must occur before any instruction/provider, including an empty typed list. Only Flow test fixtures reopen for that addition; Core/lint remain frozen. Full-gate execution awaits the final Flow freeze/pass.

## Final focused pass and review

The final focused Flow run passed 413 assertions, exit code 0; [terminal output](evidence/034-focused-flow-final-pass.json) is saved. Lint remains at 68 passing assertions. The added root-callback fixture contrasts an effectful exact root with a pure namespaced same-suffix word, checks resolved identities, and proves denied empty-list preflight occurs before instructions/providers. Independent read-only review found no material issue in frozen source/tests, including exact lookup, callback qualification, shape guards, spans/rendering, named argument order, attachments and lint traversal. The complete 24-check Release gate is now running; its result is not yet claimed.

## Final local integration

The complete fresh Release validation finished with exit code 0 and all 24 checks passing. The solution build reported zero warnings/errors. Flow passed 413 assertions, lint 68, storage 9 groups / 105 assertions, IR 102, interpreter 22, formatting 39 and language acceptance 34 groups / 583 assertions. Other source/value/business/contracts/harness/conventional/discovery/vocabulary checks and fresh-process persistence, matched fixtures, trial-host, parser limits and both whitespace checks passed.

Saved [full local evidence](evidence/034-flow-root-validation.json) plus adjacent projection, matched-fixture, host and parser reports. These identify the dirty prototype checkout based on `b04a9d6fd75ddd027ab78cd1d76f1796c51119ad`, not committed CI proof for this new source. Task-bank validation remains artifact-shape evidence (2,114 assertions / 60 tasks / 180 proposed vectors), not execution of those benchmark tasks. No agent-productivity or native-memory benefit is claimed.

Final refinement retained the existing verified semantic IR and primitive registry. Exact root calls/callbacks use explicit source representations and deterministic lookup; ordinary short-name ambiguity remains. Root-qualified callbacks cannot select a namespace alternative to repair types, output arity or denied effects. All new AST consumers and callback metadata fixtures were updated, including lint and authored cases. The change is an additive pre-durable Flow syntax-version-1 extension; Runtime/protocol remain unchanged.

Complete-project signature catalogs/lowering/call bindings and durable/default integration remain the next required migration work. The full PRD remains active.

## Committed CI and publication evidence

[CI run 37378108610](https://github.com/benwmaddox/AgentLang/actions/runs/37378108610)
passed on exact source commit `c0b33dcb2b8bb1bb6656cc1e94cb5c23539b8a0f`.
Downloaded evidence identifies a clean checkout, Release configuration and all
24 checks with exit code zero. Saved [run identity](evidence/034-ci-run.json),
[validation](evidence/034-ci-validation.json) and its four adjacent reports.
This is committed source evidence, separate from the earlier dirty local gate.

The subsequent memory-research commit `507b07a` adds only documentation and its
assessment report. The publication update saves CI evidence; executable source,
tests, scripts and workflows have no difference from the tested source commit.
Private visibility and fast-forward ancestry are checked before pushing these
updates to prototype/main. A new post-publication CI run is separate evidence
and is not claimed by this section.
