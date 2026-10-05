# Flow output-vector integration

Status: output-vector slice implemented and all 24 local Release checks passed. Committed CI/publication is tracked below. The complete PRD and frontend migration remain unfinished.

## Plan and acceptance

Implement the opt-in contract in [FLOW-OUTPUTS.md](../docs/FLOW-OUTPUTS.md): ordered nonempty output signatures, complete vector destructuring, and terminal lexical block result vectors. Keep scalar declarations and expressions compatible, reject implicit spreading and partial bindings, and reuse existing typed Core/verified IR rather than introducing tuple values or a second execution model.

Require exact positional output types and counts at branch/case joins. Calls resolve from names and input types before checking output arity. Scalar contexts require exactly one value. Destructuring binds in signature order using reverse stack stores, evaluates the producer once, and retains authored name spans. Branch returns supply branch results and do not exit the enclosing word. Shared structural preflight must include every initializer, return member, and output annotation. Lint remains advisory.

The source owner controls syntax/parser/lowering and focused Flow tests; a separate worker controls lint compatibility and tests. Root reviews the result, serializes builds, runs the complete fresh Release gate, and publishes code with updated reports only after validation. The canonical checkout is used throughout, without worktrees.

## Validation required

Run Core/Flow and lint focused checks after source freeze, then `pwsh -NoProfile -File scripts/Validate.ps1 -Configuration Release -ReportPath .agentlang/reports/030-flow-output-validation.json`. Inspect actual results and any failures, preserve raw evidence, review the final diff, and obtain committed CI before merging. No passing result is claimed for this in-progress slice.

## Remaining scope

Flow-native tests/examples, complete project lowering, exact root-word addressing, durable versioned source/history/identity integration, rollback/snapshots, and default Runtime/protocol cutover remain required. Full business providers/domain fixtures and controlled external-subagent Flat/Growing/Conventional trials are also unfinished. Output vectors alone do not establish productivity, correctness, or memory improvements.

## Review during implementation

Root review found that the initial parser required parenthesized return forms, despite the approved contract allowing scalar `return label`. The owner is preserving both forms and canonicalizing rendering with parentheses. Public isolated expression compilation also needs explicit scalar enforcement; internal vector lowering is reserved for destructuring. These findings are being addressed before any validation result is claimed.

After those changes, the owner froze Core syntax/parser/lowering and ran the Release Core build: zero warnings and zero errors. Flow execution tests and the complete integrated gate are still pending; a clean Core build proves compilation only. An independent read-only review of the frozen Core change is in progress.

The independent review found no material issue in output resolution, substitution, ordered joins, scalar rejection, reverse stores, or authored spans. It did find a new wide-vector preflight gap: destructuring names were scanned/copied before charging the node budget, and signature output lists were eagerly mapped before scheduling. The new statement-shape preflight also copied the whole block before its expressions were charged. The owner reopened FlowSyntax and focused tests to stream this validation, charge binding members, and reject oversized host-built signatures/patterns/blocks before bulk traversal. The earlier clean Core build predates this repair and is not final validation evidence.

After repair, root rebuilt Core Release: zero warnings/errors. Independent follow-up review confirmed the streaming roots, binding charges, and one-at-a-time block scheduling resolve the findings. The first focused Flow test build then failed with F# `FS0001` at three new coverage inspections: `FunctionBody` is `IrBlock`, while `VerifiedIrBody.inspect` requires `VerifiedIrBody`. The owner is fixing those test API calls and repeating the focused suite. No execution pass is claimed from this failed build.

The owner changed those assertions to inspect `CoverageByWord` on the verified program. The focused Flow Release rerun passed 261 assertions. Root's focused Flow-lint Release run passed 66 assertions, including vector initializer visibility, own spans, nested return reads, and declaration-ordered diagnostics for reversed return members. The complete fresh 24-check Release gate is now running; its result is not yet claimed.

A separate read-only compiler investigation identified the existing complete-snapshot compiler as the final batch authority, with acyclic call policy and exact source-origin/identity contracts. The next implementation contract is saved in [FLOW-PROJECT-LOWERING.md](../docs/FLOW-PROJECT-LOWERING.md). That document is evidence-backed planning, not an implemented batch API or durable Flow feature.

## Parent post-merge CI runner failure

The first main run for parent commit `5f955561cea7b00972938979aff10ca2e82f4914` failed before any workflow step ran. GitHub's annotation states: "The job was not acquired by Runner of type hosted even after multiple attempts". The same exact commit passed [prototype CI 37364211049](https://github.com/benwmaddox/AgentLang/actions/runs/37364211049). Root requested a failed-job rerun of main run 37364214698; the result remains pending. Saved first-attempt [run identity](evidence/030-parent-main-ci-first-attempt.json), [annotation](evidence/030-parent-main-ci-first-annotations.json), and [same-commit prototype result](evidence/030-parent-prototype-ci.json). This is a hosted-runner failure, not a failing language validation command.

The second attempt also terminated without acquiring a runner or executing any step. Its [result](evidence/030-parent-main-ci-second-attempt.json) and [annotation](evidence/030-parent-main-ci-second-annotations.json) are preserved. No further immediate rerun was requested; local implementation and validation continue independently. The earlier main result must not be described as a passing post-merge gate.

## Final local integration

`pwsh -NoProfile -File scripts/Validate.ps1 -Configuration Release -ReportPath .agentlang/reports/030-flow-output-validation.json` completed with exit code 0 and all 24 checks passing. The fresh solution build reported zero warnings/errors. Flow passed 261 assertions, lint 66, IR 102, interpreter 22, and language acceptance 34 groups / 583 assertions. Source/storage, business/conventional, harness, discovery, formatting, vocabulary and task-bank validation also passed. Fresh-process persistence passed 99 checks, matched fixtures 70, trial-host checks 17, and parser-process limits 5. Working and staged whitespace checks passed.

Saved [full local evidence](evidence/030-flow-output-validation.json) and adjacent projection, matched-fixture, trial-host and parser reports identify the dirty prototype checkout based on `5f955561cea7b00972938979aff10ca2e82f4914`. The task bank's 2,114 assertions still check artifact shape for 60 tasks and 180 proposed vectors, not execution of those benchmark tasks. No controlled agent-performance or native-memory result is claimed.

Final refinement kept the source change within the output-vector contract, updated all Flow AST consumers/fixtures, and preserved the existing Core/IR contract. Scalar source remains valid; output vectors are an additive, pre-durable Flow syntax-version-1 extension. The remaining migration boundaries above still apply. Committed CI is required separately from this local evidence.
