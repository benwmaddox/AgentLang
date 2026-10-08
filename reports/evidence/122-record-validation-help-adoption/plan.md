# Record-Validation Help Adoption Trial

Status: input preparation only. Do not build, copy a runtime, run preflight, or dispatch an actor until the current help implementation is built and the coordinator approves the pinned runtime.

## Scope and question

Observe one fresh Luna/max actor completing one Flow/2 task in an empty project: can the actor use the current versioned help to discover an appropriate way to enforce a two-field record invariant, preserve the requested behavior, and commit at the highest maturity the evidence supports? Help use is descriptive process evidence. This is a single bounded observation, not a controlled comparison or a before/after causal claim. The task differs from R120; do not compare scores as if only guidance changed.

The public task is to define `BatchBounds` with public `minimum: Int` and `maximum: Int` fields. Valid values satisfy `minimum >= 1` and `maximum >= minimum`. `batch.span(value: BatchBounds) -> Int` returns `maximum - minimum`. Invalid construction must fail with `RECORD_VALIDATION_FAILED`; valid bounds include equal endpoints. Keep values small and make no overflow claim. See `actor-prompt.md` for the complete actor instructions and broker setup.

## Frozen oracle

`oracle.json` contains ten independent input pairs: four valid pairs with exact spans and six invalid pairs whose construction must fail with `RECORD_VALIDATION_FAILED`. It is coordinator-only and must not be shown to the actor. Do not change it after dispatch.

## Independent scoring dimensions

Report these separately so domain behavior, persistence, and maturity do not collapse into one result:

1. **Behavior:** Verify all ten oracle pairs in a fresh read-only runtime. Valid inputs must construct and produce their listed spans; invalid inputs must fail at construction with the expected code.
2. **Actor tests:** Inspect the attached tests and their results. Record whether tests cover valid behavior and rejected construction. Record whether the predicate named by the persisted type binding has its own true and false Boolean observations; do not infer those observations from an error in a caller.
3. **Persistence:** After the actor session closes, load the committed project in a fresh runtime. Recheck representative behavior and inspect the persisted `TypeSource.ValidatorTarget`; it must identify the current stable word identity for the predicate. This is separate from the actor's source inspection and separate from maturity.
4. **Maturity:** Record the validator and `batch.span` maturity independently. Record whether both are library. A library result for the Bool predicate should be supported by its own true and false observations. Preserve any qualification or publication refusal and its structured evidence; do not weaken the invariant or tests to obtain library status. The `Int` result may have a distinct gate.
5. **Process:** Record available versioned help topics/queries only if they appear in the broker trace. Do not require a particular query sequence or coach the actor toward validator syntax/test ownership.

## Boundaries

Use one fresh `gpt-6-luna` actor at maximum reasoning, no inherited conversation, one broker session, and at most 100 exchanges. Reuse `scripts/Start-SubagentTrialHostV2.ps1`, the report117 Flow/2 primer, and the prior restricted operation allowlist. The broker may access only the isolated actor project. Do not read or edit repository source, other trials, reports, frozen controls, scorer/verifier material, or the oracle during the actor session. Do not spawn subagents or use web tools. Preserve the full broker trace and final response. The coordinator may inspect the actor's committed source through the broker and independently score the committed project after close.

Do not modify frozen R120 files. This preparation slice consists only of this plan, the actor prompt, and the oracle; no runtime copy, actor project, runner, preflight, build, or dispatch is part of it.
