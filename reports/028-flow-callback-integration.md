# Flow static-callback integration

Status: this callback and API-hardening milestone passed the final fresh 24-check Release gate. The complete PRD and default frontend migration remain unfinished.

The previous source/report milestone at `dcd05d14b9b3c17d75de8bb02abe75f49e48788b` passed post-merge [main CI 37354067757](https://github.com/benwmaddox/AgentLang/actions/runs/37354067757); identity/result is retained in [parent CI evidence](evidence/028-parent-ci-run.json).

## Plan and acceptance

Extend Flow with explicit, statically resolved list callback references: `.map(customer::normalize)`, `.filter(customer::active?)`, `.each(email::send)`, and an explicit `word` marker for short callback names. Ordinary `.map(localValue)` remains ordinary receiver-call syntax. A callback reference is dictionary metadata, not a first-class function or a lexical local read. Lower directly through existing `MapList`, `FilterList`, and `EachList`, with no generated words or closures.

Require the receiver to be `List<T>` and the target signature to be exactly `T -> U`, `T -> Bool`, or `T -> Unit` respectively. Reject malformed, unknown, ambiguous and multi-output targets deterministically. Include callback dependencies/effects even for empty lists; deny disallowed effects before any provider invocation. Preserve authored operation spans and library branch/instruction obligations without attributing callback-body coverage to the caller. Lint must visit receivers and ordinary expression arguments while ignoring dictionary target references as local reads.

Focused parser/render/lowering/interpreter/coverage/error checks and the full fresh 24-check Release gate are required before commit/push. Store actual results below, including failed attempts. Root owns lint compatibility and publication; the Flow owner owns syntax/lowering/focused cases. A separate read-only plan maps Flow-native tests/examples for the next slice.

## Remaining requirements

Output vectors/destructuring, Flow-native tests/examples and complete project lowering remain stage-2 work. Durable source versions, identity-safe dictionary re-resolution and replacement, history/reload/rollback/snapshot integration, then default Runtime/protocol/CLI cutover remain required. Existing task-bank shape tests are not executed benchmark acceptance cases. No new controlled agent-performance or native-memory result is claimed by this slice.

## Parser review during implementation

A source review found an eagerly evaluated diagnostic fallback in the new mixed-callback argument path: it used `Option.get` on the current token even when the callback span had already been found. An incomplete call ending after an extra argument could therefore throw at EOF. The owner is replacing the fallback with safe span selection and adding an incomplete mixed-reference regression before validation. No passing result is claimed for the in-progress code.

Callback lookup review also identified signature-filtered short-name selection. The approved word-reference contract resolves identity by name first and rejects multiple matching short names before validating inputs/outputs. In particular, a filter's required Bool output cannot silently select one of two same-named callbacks. The owner is simplifying lookup and adding ambiguity regressions; generated type-call aliases are not callback word identities.

## Initial focused result

The Flow owner's fresh Core Release build passed with zero warnings/errors. After adding EOF-aware callback lookahead, the focused Flow Release suite passed 172 assertions. This includes valid partial qualified/short callback input, nominal Email/base-type separation, type-changing maps, effect/dependency preservation on empty lists, direct IR operations and ambiguity rejection. Remaining focused suites and the complete fresh repository gate are still required before publication.

## First integrated gate and independent review

The fresh Release validation gate completed with exit code 0 and all 24 required checks passing: Flow 172, lint 47, IR 102, interpreter 22, and language acceptance 583 assertions. The complete output is retained in [first gate evidence](evidence/028-callback-first-gate.json). This gate validates the callback slice before the additional API hardening below, not the later final source state.

Independent read-only review found no callback execution/effect/source regression in the parsed-source path. It confirmed ordinary value arguments, exact callback identity lookup, conservative effects/dependencies, authored callback sites, and lint traversal. It also found that public lowerer/rendering APIs can receive manually constructed expression trees beyond the parser's depth guard. A shared iterative preflight is being added at those public boundaries and reused by the parser; focused and integrated validation must be repeated afterward.

The review identified a separate, fail-closed name-addressability limit: a one-segment authored word cannot be selected exactly when another word shares its short suffix. Qualification selects namespaced identities but has no spelling for that root identity. This must be resolved before durable/default source-identity guarantees; strict ambiguity rejection is retained rather than silent selection.

## Expression-preflight integration (before type/node hardening)

After shared preflight hardening, `pwsh -NoProfile -File scripts/Validate.ps1 -Configuration Release -ReportPath .agentlang/reports/028-flow-callback-final-validation.json` completed with exit code 0 and all 24 required checks passing. The fresh solution build reported zero warnings/errors. Flow passed 185 assertions, lint 47, IR 102, interpreter 22, and language acceptance 34 groups / 583 assertions. Source/storage, value inspection, business, conventional, harness, discovery, formatting, vocabulary and task-bank checks passed, along with fresh-process persistence (99), matched fixtures (70), trial-host checks (17), parser-process limits (5), and working/staged whitespace checks.

The exact output is retained in [expression-preflight gate evidence](evidence/028-expression-preflight-gate.json). Its metadata identifies the dirty prototype checkout based on `dcd05d14b9b3c17d75de8bb02abe75f49e48788b`; committed CI remains a separate publication check. The task-bank's 2,114 assertions still validate artifact shape for 60 tasks and 180 proposed vectors, not execution of those benchmark tasks. No agent-productivity comparison was run in this milestone.

Callback identity resolution, ordinary calls, exact nominal inputs, closed output types, conservative effects/dependencies, raw loop coverage, authored source sites, incomplete REPL input, and shared depth limits are validated in the focused suite. Flow remains opt-in; durable Flow library commit/reload and default protocol authoring remain outstanding. The next source contracts are recorded in [Flow attachments](../docs/FLOW-ATTACHMENTS.md) and [output vectors](../docs/FLOW-OUTPUTS.md).

## Publication guard and full structural preflight follow-up

Before publication, explicit `gh repo view benwmaddox/AgentLang` unexpectedly reported `PUBLIC` / `isPrivate=false`, despite the earlier private checks. The authorized private-repository setting was restored with `gh repo edit --visibility private --accept-visibility-change-consequences`. Repository metadata and the REST API then both confirmed private; [restored visibility evidence](evidence/028-private-visibility-restored.json) records the latter. The event query showed an earlier PublicEvent, but did not establish the cause of this later observation. No push occurred while this guard reported public.

A follow-up review found recursive type annotations outside the expression-only guard: container type arguments and word parameter/output types. The shared preflight is being extended to those authored LangType trees, with the same depth limit and an expanded structural-node budget to bound host-built shared expression/type DAGs. Arbitrary host compiler contexts are not reclassified as untrusted authoring input by this change. The earlier 185-assertion, 24-check gate is preserved as [expression-preflight gate evidence](evidence/028-expression-preflight-gate.json); fresh validation is required after this last follow-up.

## Final type/node validation

The shared iterative preflight now limits expression/type depth to 128 and expanded nodes to 100,000, with one aggregate budget per word. The first focused run exposed a test-fixture mistake: a branching shared Result type exceeded the node budget before reaching its intended depth boundary. The valid depth-boundary fixture was changed to unary nested List; branching DAGs remain budget rejection cases. The corrected focused suite passed 198 assertions.

The final fresh Release gate completed with exit code 0 and all 24 checks passing, including a solution build with zero warnings/errors, Flow 198, lint 47, IR 102, interpreter 22, and acceptance 583 assertions. The complete result is saved in [final gate evidence](evidence/028-flow-callback-final-validation.json). Adjacent reports retain fresh-process persistence (99), matched fixtures (70), trial-host checks (17), and parser-process limits (5). The latter two scripts selected their `retry-01` output names because prior evidence existed; those newly produced artifacts are copied to the stable report filenames, rather than reusing stale files.

The mailbox static-data memory candidate remains recorded in the [PRD](../docs/PRD.md), [stack-only research plan](../docs/STACK-ONLY-RESEARCH.md), and [assessment report](020-arena-allocation-assessment.md). No allocator or memory-performance claim is included in this code milestone. Committed CI and publication are still required.

Final independent read-only review found no remaining material issue in the structural guard: expanded occurrences are counted before enqueueing, types/body share the word budget, public render/lower/check/compile routes validate before recursion, and recursive parser limits remain intact. The review ran no builds and made no source edits.

## Committed CI publication evidence

Source/report commit 6cffcd1f04d3031f8d77ce970136b80d61ccb7d4 passed [CI run 37363667185](https://github.com/benwmaddox/AgentLang/actions/runs/37363667185). The downloaded artifact names that exact commit, reports a clean checkout, and passes all 24 checks. Saved evidence: [run identity](evidence/028-committed-ci-run.json) and [committed validation](evidence/028-committed-ci-validation.json). This follow-up changes only reports/evidence; executable source is unchanged.
