# 135 — R09-inspired shared-summary comparison

## Result

All six fresh participants passed the thirteen independent public-summary
scenarios. Three also met the frozen full-acceptance rubric: both F# participants
and the second retained-vocabulary participant. The other three passed public
behavior but each missed a different criterion: the first retained participant
ran out of exchanges before finalization; the first reset participant used a
helper signature omitted from the public task but required by the private rubric;
and the second reset participant left duplicate aggregation in the account
summary. These outcomes do not establish comparative reliability superiority.

| Condition | Public behavior | Shared aggregation and reuse | Completion | Frozen full acceptance |
| --- | --- | --- | --- | --- |
| Retained, first participant | 13/13; persisted helper and summaries pass | Both summaries call the unchanged seed operation. A new alias is unused; reuse followed an initial reimplementation. | Stopped at the 100-exchange cap before `task.commit` and clean `host.close`. | No: workflow incomplete. |
| Reset, first participant | 13/13 in summary-only mode | One new `Store, Customer` helper serves both summary paths through the inherited payment step. | Helper library commit and task finalization passed. | No: private rubric required `Store, CustomerId`, a parameter contract absent from the dispatched task. |
| Conventional F#, first participant | 13/13 | Both summaries call the supplied `Store.customerPaidTotal`. | Tests and task finalization passed. | Yes. |
| Retained, second participant | 13/13 | Both summaries call a new library alias that delegates to the unchanged seed. The alias adds vocabulary without new business behavior. | 190 attached tests, `task.commit`, and clean `host.close` passed. | Yes. |
| Reset, second participant | 13/13, including direct-helper behavior | Dashboard calls the new helper, but account summary still folds payments independently; the helper repeats that fold. | 182 attached tests, `task.commit`, and clean `host.close` passed in 67 broker exchanges. | No: shared structure is incomplete. |
| Conventional F#, second participant | 13/13 | Both summaries call the supplied `Store.customerPaidTotal`; the existing checked dashboard fold remains. | Tests and task finalization passed. | Yes. |

Public behavior, structure, library qualification where applicable, tests and
workflow completion are separate evidence. Passing the oracle alone does not
establish the requested refactor. The reset-signature mismatch is an evaluation
limitation, not evidence of a public behavior defect. The second reset result is
a structural failure despite its passing behavior and tests. That incomplete
shared refactor is an observed task failure. With two participants per condition
and the reset-signature limitation, this study does not establish general
comparative reliability.

## Study and scoring

The study tests whether an agent discovers or creates one typed customer
payment-total operation and uses it to refactor two initially correct summaries.
The conditions are retained AgentLang vocabulary, the same language foundation
without the seed, and conventional F# with an equivalent unused helper. This is
an exploratory refactoring and discovery comparison, not a repair benchmark or
a native runtime performance study.

The retained start preserves the seed's function IDs, bodies, tests and history;
the F# start likewise supplies its equivalent helper unused by the summaries.
Hidden expected values stay outside participant projects. The frozen acceptance
procedure checks direct helper and summary results, unrelated behavior,
source-level shared structure, attached tests, library qualification for
reusable AgentLang functions, and task finalization. Reuse of the retained helper
is reported separately; a correct new helper is permitted. Unused legacy
callbacks need not be deleted through a broker that cannot delete persistent
functions.

Predispatch review repaired scorer restrictions before any participant ran:
AgentLang scorers allow additional tests and any valid helper name with the
required type and active call sites; candidate compile, test and evaluation
failures are separated from harness faults. The F# selector was changed to
allow private helpers and modules while preserving nominal signature checks.
Baseline, correct, alternate-name and missing-target controls were exercised.
Wrong-count controls were rejected by the independent nonzero-count case even
when their copied attached tests were weakened. These controls validate the
scorers; they are not participant outcomes.

All three first-round actors started from frozen copies, used `gpt-6-luna` with
max reasoning and no inherited conversation. Under the prespecified stopping
rule, a second fresh participant per condition ran because the first round did
not fully satisfy the task in all three conditions. Replica two used the same
immutable starts, prompts, tools, budget and scoring rules, with only mechanical
path substitutions. It was not seeded from the first participants' work.

The first-round input freeze is recorded in
`.agentlang/r09-discovery-001/comparison/input-freeze.json` (SHA-256
`f69ef6afa08cd6316c38146bf4b7b11aec051c5fd6b75ab6d57350a5af8dfed5`); its
dispatch is in `dispatch-events.json`. The second-round freeze is
`replica2-input-freeze.json` (SHA-256
`41b4b803c6e3965f144fe67ca8982279807b76e303aa75f1360ae2faaec08d81`). Before
dispatch, 1,398 first-round artifact/start-copy hashes were checked; before replica two,
680 actor-copy hashes and the prompt/wrapper substitutions were checked. The
frozen AgentLang scorer has source hash
`3d8e6355fd98cdf67f4043f13f9d284481ab01a107fbab7852b250a20ea8c204`. The
acceptance details and source-review criteria are in the
[frozen acceptance procedure](../experiments/AgentLang.SubagentTrials/r09-discovery-001/comparison-acceptance.md).

## Outcome notes

**First retained participant.** The actor used all 100 permitted broker
exchanges; request 101 was rejected with `TRIAL_EXCHANGE_LIMIT`. Both persisted
summary paths call the unchanged `customer.paid-total`; the added
`customer.payment-total` alias is unused. Fresh scoring of that actual shared
operation and both summaries passes all thirteen scenarios and 91 checks, with
193 attached tests. The source was not repaired, but the required task commit
and clean close did not happen. The actor initially implemented a second lookup
and fold, then found and reused the seed. Its final handoff also misstated the
dashboard revision's commit state: the trace records a passing test at exchange
87 and successful replacement at 88. The trace and final manifest establish the
persisted state.

**First reset participant.** The public task named the two summary signatures
and unknown-customer behavior, but did not specify a helper signature. The actor
created `customer.payment-total(Store, Customer)`, resolved the customer, and
used the same helper for account and dashboard summaries. The private rubric and
direct-helper scorer instead require `Store, CustomerId`. Summary-only scoring
passes 13/13 scenarios and 76 checks, including a fresh 179-test suite. The
combined helper mode reports zero fully passing cases because the direct calls
are ill-typed (`CustomerId` supplied where `Customer` is expected); it does not
report thirteen wrong business results. The helper is library-qualified, the
active summary paths share it, inherited content is preserved, and finalization
passes. Preserve the frozen-rubric rejection while treating the signature gap as
a study-design limitation, not a reliability disadvantage.

**F# participants.** Both participants reused the supplied
`Store.customerPaidTotal` in account and dashboard summaries, preserved the
existing checked dashboard fold and unrelated behavior, passed all thirteen
scenarios, and completed their sessions. Fresh local tests passed 18 groups /
153 assertions for the first participant and 18 groups / 157 assertions for the
second. The first trace contains three recovered argument, hash and patch-anchor
errors. The second contains 16 error envelopes: twelve consecutive invalid JSON
frames came from one malformed send; four other requests were rejected. The
actor handoffs omit some recovered errors, so traces remain the source for that
history. These errors did not invalidate the passing implementations.

**Second retained participant.** Its new `customer.payment-total(Store,
CustomerId)` is library-qualified and delegates to unchanged
`customer.paid-total`; both summaries call the alias. The direct-helper and
summary scorer passes 13/13 scenarios and 91 checks, and the fresh attached
suite passes 190 tests. The alias records successful reuse while adding no new
aggregation behavior.

**Second reset participant.** The actor handoff claims both summaries use
`customer.payment-total`, but the saved dictionary shows otherwise. The account
summary still performs its own lookup and payment fold; the new helper repeats
that fold; only the dashboard calls the helper. The independent behavior scorer
passes 13/13 direct-helper and summary cases with 182 attached tests. Thus the
behavior score is valid, while the source-level shared-structure requirement
fails. The 67-exchange trace records `task.commit`, clean `host.close`, and zero
host/runtime exit codes.

## Discovery and limits

The two initial Flow participants requested `words` early. The runtime already
offers a compact names-only `words` option, documented in README and
`docs/DISCOVERY.md`, but the frozen primer does not mention it and neither actor
selected it. This is an exposure observation; it does not show that compact
discovery is absent or that an instruction was ignored. The F# source read also
placed the supplied helper immediately before the dashboard function, while a
Flow `source` request returns one selected function. These discovery surfaces
are not matched, so the study cannot isolate their effects from agent choices,
language familiarity or tool behavior.

The report 134 seed passed its own acceptance study; that is setup evidence, not
a comparison-agent outcome. The sample is small and outcome-dependent: each arm
has two fresh participants after the first round did not yield full acceptance
in every condition. No token, context, throughput or performance advantage is
measured here, and these data establish no comparative reliability superiority.

The next smallest efficacy step is to improve discovery/default help and make
the structural requirement explicit and mechanically checked, then evaluate
that change with a held-out task. Do not run more replicas of this same case.
Native runtime throughput remains a separate experiment.

Evidence is retained under `.agentlang/r09-discovery-001/comparison/`, including
frozen inputs, dispatch traces, participant projects, scorer runs and source
inventories. The final evidence package is available as the
[report 135 archive](evidence/135-r09-shared-summary-comparison/135-r09-shared-summary-comparison.zip).
