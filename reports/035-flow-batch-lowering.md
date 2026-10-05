# Flow batch signature catalog

Status: the opt-in compiler batch API and focused Flow tests are implemented. The focused Flow suite passes with 473 assertions, and the full fresh Release validation gate passed all 24 local checks. No durable publication or default frontend cutover is claimed.

## Delivered behavior

`FlowLowering.compileBatchWords` lowers an ordered set of Flow word additions and replacements against one signature catalog and compiles the resulting real dictionary once. Revision intent is explicit: an add supplies a new stable `WordId` and revision; a replacement supplies the expected existing revision and an advanced revision. Replacements retain their stable identity, status, and maturity, and keep `WordEntry.Revision` synchronized with `WordDefinition.Revision`.

The body-free catalog contains ordered inputs/outputs, declared effects, primitive/generated/user kind, named parameters, identity, revision, and declaration span. It supports reverse-order forward ordinary calls, dot stages, static list callbacks, generated constructors/accessors, existing primitive generics, and named argument evaluation order. All proposed bodies are then lowered as real definitions. Final compilation checks retained callers, output types, effects, scalar validators, call cycles, IR, and source origins against the complete context.

Before creating the overlay, the API validates exact base word/ID key coverage, global nonempty unique IDs, synchronized revisions, parameter-catalog keys and arities, type/effect vocabulary, proposed names/signatures, and the full input source-origin key/value map. One marker allocator spans the batch. The final origin map is rebuilt from final real bodies, so replaced-body markers are dropped only after the input snapshot passes validation.

The result is a compilation artifact only. Runtime protocol, durable files/history, project identity allocation, manifest/version checks, publication, Flow attachments, and default frontend selection are unchanged.

## Acceptance added

The Flow suite exercises reverse-order forward ordinary/dot/callback resolution and checks verified IR target identities. It also covers primitive generic specialization, generated constructor/accessor identities, record-field-derived constructor parameter names, named argument evaluation order, vector producer/destructuring behavior, scalar use rejection, and final effect/cycle/scalar-validator checks.

Replacement tests verify stable ID and status/maturity preservation, revision synchronization, a changed replacement signature used by a same-batch sibling, and rejection when an unchanged caller no longer type-checks. Preflight tests cover duplicate names/IDs, add/replacement collisions, missing/protected targets, wrong/stale IDs or revisions, non-advancing revisions, malformed host-built names, base dictionary/ID mismatch, duplicate IDs, bad parameter metadata, open/unknown types, unknown effects, and empty batches. Origin tests cover exact pruning, retained markers, disjoint new markers, stale/missing input mappings, and invalid mappings owned by a replaced word.

The generated-constructor metadata regression includes both a malformed explicit parameter list that must fail preflight and a valid-but-different list where record field names remain authoritative. Authored call-binding metadata is not implemented: the batch compiler does not yet prove that unchanged dot stages remain bound to the same target after a vocabulary change. That stability check, and attachment compilation against the final snapshot, remain prerequisites before durable publication.

## Validation

The first Core Release build caught F# inference errors in signature/type validation helpers and revision-intent handling. After explicit type annotations and the later hand-built word-name guard, the fresh Core Release builds passed with zero warnings and errors. See the preserved first failure and successful rebuilds in [the Core evidence](evidence/036-early-core-build-failure.json), [the review-fix build](evidence/036-core-review-fix-build.json), and [the name-guard build](evidence/036-core-name-guard-build.json).

The first focused Flow attempt failed on an offside test-fixture application; the next failed on ambiguous F# record inference. After those were corrected, two execution attempts exposed test-fixture mistakes: an assertion expected the retained caller name when the compiler reports the resolved callee, and an origin replacement used revision 1 although `compileWord` had assigned revision 0. Both fixtures were corrected without changing compiler behavior. The corresponding outputs are preserved in [the first Flow build failure](evidence/036-focused-flow-build-failure.json), [the type-inference failure](evidence/036-focused-flow-type-build-failure.json), [the first execution failure](evidence/036-focused-flow-first-execution-failure.json), and [the origin fixture failure](evidence/036-focused-flow-origin-fixture-failure.json).

The final focused Flow command, `dotnet run --configuration Release --project tests/AgentLang.Flow.Tests`, passed with 473 assertions and exit code 0; see the [saved output](evidence/036-focused-flow-pass.json). The coordinating agent also reported focused IR (102 assertions), interpreter (22 assertions), and language acceptance (583 checks) passing.

The fresh Core Release build command, `dotnet build src/AgentLang.Core/AgentLang.Core.fsproj --configuration Release --no-incremental`, passed with zero warnings and errors after the final name-guard change; see [the final Core build output](evidence/036-core-name-guard-build.json). The full fresh Release gate, `pwsh -NoProfile -File scripts/Validate.ps1 -Configuration Release -ReportPath .agentlang/reports/036-flow-batch-validation.json`, completed with exit code 0 and all 24 checks passing. The solution build had zero warnings/errors; Flow passed 473 assertions, lint 68, IR 102, interpreter 22, language acceptance 34 groups / 583 assertions, and storage 9 groups / 105 assertions. Projection, matched fixtures, subagent host, parser limits, and diff whitespace checks also passed. See the [captured validation evidence](evidence/036-flow-batch-validation.json). The report records revision `84611d5` with `dirty: true`, so it documents local validation of the milestone state, not clean committed-revision CI evidence.

## Limits and next work

The signature catalog resolves proposed words, but source and parameter-name metadata still live beside legacy compiler objects. The batch result has no durable call-binding sidecar, does not compare unchanged authored call identities before/after replacements, and does not compile tests/examples as part of the batch API. It therefore cannot yet be used as a complete durable project update. The next project-lowering step must add authored call bindings, rebinding collision detection, and final-snapshot attachment compilation before storage/publication work.

This milestone provides compiler-level evidence for the signature-catalog design, not evidence about agent productivity, context compression, memory use, or the language research hypothesis.
