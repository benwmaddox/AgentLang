# Preview validation with and without a retained abstraction

2026-10-10. Two fresh Luna/max agents completed the same held-out task and both
passed all 33 independent cases without changing prior definitions or tests.
The retained-vocabulary agent discovered and reused the earlier agent's
`subscription.handoff`. The reset agent composed the existing cancellation and
creation functions directly. This supports task-specific discoverable reuse;
it does not establish a reliability advantage or lower context requirements.

## Comparison

The task adds pure `subscription.preview-replacement`: apply cancellation rules,
then replacement-creation rules, preserving their error precedence. Successful
validation returns the complete original Store rather than the changed Store.
Failure forwards the original error. Existing public definitions/tests must stay
unchanged, and the new function must qualify as library vocabulary.

The reset seed is report 163's accepted pre-handoff project: 214 library-visible
words and 137 tests. The retained seed is that report's accepted agent result:
215 words and 142 tests. The treatment includes the handoff function, five tests,
source/history metadata and associated discoverability. Both already contain
substantial domain vocabulary; reset is not a primitive-only Flat condition.
The retained seed does not differ by only one function's bytes.

Both seeds were copied and revalidated on the same freshly built, pinned CLI,
with no seed-file changes. Two fresh `gpt-6-luna`/max participants had no inherited
task context. The frozen order was randomized to reset then retained; dispatch
was sequential. Prompts and current Flow/2 guidance were identical apart from
isolated launch paths. Each broker allowed the same operations, at most 100
exchanges, virtual filesystem providers and a fixed clock. The public task did
not name the retained handoff helper. Both brokers closed with host/runtime exit 0.

The original full Release gate was still live during seed/control preparation.
CLI/Core sources were unchanged from the accepted baseline, its fresh build and
relevant interpreter checks had passed, and all runtime files were pinned.
All 37 Release checks subsequently passed before the first participant broker
started. The predeclared protocol excludes latency/throughput comparisons.

## Independent acceptance and preservation

The scorer reuses report 163's independent model and 33 fixtures: 26 common
valid-reference cases and seven separately classified orphan-reference cases.
For model success, the expected output is the original input Store; failures
retain the model's error code. Complete canonical input/output Store projections
cover customers, products, subscriptions, invoices, payments and email state.
Error-code acceptance is not a comparison of every error-record field; source
review separately confirms both implementations forward the complete original
error value without rebuilding it. This is a held-out task over reused fixtures,
not a new unseen corpus.

Before participant dispatch, both correct controls passed 33/33. A wrong-order
control executed every case and failed three precedence cases. A control leaking
the cancelled intermediate Store executed every case and failed sixteen cases.
The first setup attempt failed `COMMIT_TEST_REQUIRED`: persistent development
functions also need an attached passing test. The accepted controls add one
happy-path test and commit as project functions; their independent 33-case scores
are separate from participant library qualification. The failed attempt is retained.

Both saved participant implementations passed 33/33. Exact manifest comparison
preserved all prior word rows, revisions (including tests/examples), types and
source-object bytes: reset retained 47 authored word rows and 254 objects;
retained preserved 48 rows and 261 objects. Each added only the requested library
function. Stable call bindings show the retained function directly invokes the
pre-task handoff identity; reset calls the pre-task lookup/cancel/start identities
and does not reconstruct a handoff helper.

Independent post-trial reloads passed every attached test: reset 141/141 and
retained 145/145. Both local CLI processes exited 0. These complete-suite checks
supplement the participants' different choices of focused versus full testing.

## Observed results

| Observation | Reset abstraction | Retained abstraction |
|---|---:|---:|
| Independent cases passing | 33/33 | 33/33 |
| Prior definitions/tests/types preserved | Yes | Yes |
| New function's own tests | 4 | 3 |
| Own instructions covered | 30/30 | 12/12 |
| Own branch outcomes covered | 6/6 | 2/2 |
| Result return tags observed | ok, error | ok, error |
| Runtime protocol exchanges | 19 | 18 |
| Request payload bytes | 6,355 | 3,899 |
| Response payload bytes | 56,915 | 84,334 |
| Reuses the retained handoff | No | Yes |

The retained wrapper has fewer own instructions and branches to implement/test,
while its dependency's existing implementation and tests remain necessary.
These counts do not measure transitive execution work, runtime speed or model
reasoning. Retention did not produce a material exchange-count reduction here;
it returned more protocol bytes, partly because its participant ran the full
145-test suite whereas reset ran focused new-function/cancel/start suites.
Protocol bytes are not LLM tokens or supplied context-window measurements.

Both participants encountered one `HELP_INVALID_ARGUMENT` and recovered. The
shared primer incorrectly included `frontend` in a help request; that field is
supported for authoring/evaluation but not help. This coordinator guidance error
is preserved and should not be attributed to an intrinsic language defect.
Neither participant had a rejected definition or failing test batch. Both
completed normal library commit and task commit.

The retained implementation is a small wrapper around discovered vocabulary:

```flow
fn subscription.preview-replacement(store: Store, oldId: SubscriptionId, replacementId: SubscriptionId, term: String, handoffAt: Instant, expiresAt: Instant) -> Result<Store, BusinessError> {
    effects none
    doc "Validates cancellation and replacement creation at the handoff instant, then returns the original store unchanged."

    match .subscription.handoff(store, oldId, replacementId, term, handoffAt, expiresAt) {
        ok _ => result.ok<Store, BusinessError>(store)
        error failure => result.error<Store, BusinessError>(failure)
    }
}
```

## Interpretation and next work

An agent-created abstraction survived a task boundary, was found without being
named in the next task, and reduced the new function's own logic/test surface.
Both agents were correct, so this pair demonstrates feasible reuse rather than
superior reliability. It is one easy composition task, with one sample per
condition, extra tests/history in the retained treatment and no conventional
arm. It does not overturn report 151's mixed efficacy findings or reports
166/168's lost-assertion findings during maintenance. No authoritative model
token usage, small-context advantage, native performance or service-readiness
claim follows from it.

Close this bounded composition probe. Correct the help primer for future runs;
next efficacy comparisons should stress meaningful changes to shared rules and
their callers, retaining independent behavior and exact old-test preservation.
Do not gate secondary native refinement/JIT work on more easy composition pairs.

## Evidence

The [verified archive manifest](evidence/171-preview-vocabulary-retention/archive.json)
and [evidence archive](evidence/171-preview-vocabulary-retention/evidence.zip)
retain frozen inputs, prompts, protocol traces, participant projects, controls,
failed setup, independent scores, preservation audits and complete-suite reloads.
Runtime binaries are excluded; their pre-dispatch hashes remain in the freeze.
