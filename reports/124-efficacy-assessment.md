# 124 — Efficacy assessment: workable vocabulary, unproven advantage

The prototype is usable enough for agents to build, discover, reuse and repair
strongly typed vocabulary. Its current evidence does not establish more reliable
edits than a well-designed F# repository. The next priority should be a small
matched comparison on an unfamiliar change, rather than more language features
as prerequisites for comparison.

This assessment prioritizes reliable changes and useful retained vocabulary.
Token cost is secondary. Counts from different study protocols are not pooled.
Implementation tests, agent-written tests and independent acceptance are kept
separate.

## What the experiments establish

| Question | Evidence | Interpretation |
| --- | --- | --- |
| Can fresh agents use the environment? | [064](064-repeat-agent-purpose-review.md): all 15 trials across three modes passed five small tasks. [111](111-current-syntax-stateful-comparison.md): both Flow/2 and F# actors completed the stateful reminder task. | Feasible external-agent workflow, including current syntax. High success in every condition gives little discrimination. |
| Do agents discover and reuse prior vocabulary? | [099](099-quick-agent-comparison.md): the retained actor inspected and called a previously accepted premium classifier. [109](109-location-unhinted-repair.md): actors found an unnamed repair target; retained kept the classifier call. | Actual reuse is observed. It is not unique to this language; [064](064-repeat-agent-purpose-review.md) also records conventional reuse of prior helpers. |
| Does retention improve reliability? | [109](109-location-unhinted-repair.md): all three conditions improved from 42/54 to 54/54 independent cases. Language regressions passed 166/166 and 158/158 respectively. | No superiority established. One actor per condition and unequal helper work limit the comparison. Old full-study metadata checks remained inapplicable/failed, separately from behavioral success. |
| Do library gates guarantee correct behavior? | [111](111-current-syntax-stateful-comparison.md): a scripted extra-write mutant passed all four own tests and full coverage, but independent acceptance rejected its write count. | No. Executing each path does not establish the right expectation or observable IO contract. |
| Can agents use stronger behavioral assertions? | [115](115-versioned-help-adoption.md): after explicit Flow/2 help exposure, a fresh actor wrote effect-count assertions; three tests rejected the known extra-write mutant and blocked replacement. | A bounded positive adoption result. Its independent state checks are distinct from actor-written count assertions; general event ordering was not established. |
| Can the environment support stronger reusable contracts? | [117](117-finite-coverage-adoption.md): an actor tested all Boolean combinations, while honestly keeping an unreachable Option alternative at project maturity. [122](122-record-validation-help-adoption.md): both record validator and caller qualified, with 10/10 independent cases and 6/6 tests after reload. | Useful constraints and recovery are observed, alongside limitations. These trials are not controlled comparisons against F#. |

The strongest negative result is also actionable: tests can encode the wrong
policy while enjoying complete structural coverage. In report 109, both language
agents also wrote incorrect boundary expectations during repair. Independent
expected values remain necessary; replacing them with self-generated tests would
misrepresent reliability.

The raw evidence inspected for this assessment includes report 109's
[language score summary](evidence/109-location-unhinted-repair/summaries/language-score-summary.json)
and report 111's [outcomes](evidence/111-current-syntax-stateful-comparison/results/outcomes.json)
and [mutation rejection](evidence/111-current-syntax-stateful-comparison/results/extra-write-acceptance.json).
Report 111's 126 and 34 verifier checks are heterogeneous checks, not different
numbers of business scenarios and not comparable success denominators.

## Discoverability is part of correctness support

The language can expose a capability without agents finding it. Reports 114 and
122 returned older-version help while the actors authored Flow/2. Report 120's
agent also inferred that a validator could never return false, although its
unchecked construction candidate can be rejected. Report 121 added an executable
example. In [report 123](123-versioned-record-help.md), a fresh actor explicitly
received that help and produced two library functions, seven passing tests and
10/10 independent cases with no structured errors. This establishes usable
exposure and adoption; it does not identify a causal advantage from two samples.

Unversioned help intentionally defaults to Flow/1. A Flow/2 actor must remember
a separate selector; omitting it creates a workflow mismatch, not a protocol
violation. For immediate experiments, pin and verify the help version
in the compact primer. A future protocol change should make session/front-end
version selection consistent, with explicit compatibility behavior. That design
is a candidate, not a silently adopted default change.

## What is not established

- General reduction in incorrect accepted edits, collateral regressions or error-recovery cost relative to F#.
- A benefit from accumulating vocabulary as projects become larger or less tidy.
- Robustness at 2k/4k/8k context limits, or a measured model-token advantage.
- Production memory, throughput, startup or safety from the agent-edit trials.

The cost observations are mixed. In report 099, retained used 58 exchanges,
sparse 33 and reset-rich 43. Report 064 had a favorable task-specific reuse
signal, but it did not establish a general advantage. Interface exchanges and
payload bytes cannot substitute for comparable provider usage or controlled
end-to-end timing.

The LLVM/arena probes are separate implementation evidence. A supported native
subset exists; general JIT, release packaging and the selected mailbox memory
policy are not delivered by these agent studies. Keep native work secondary to
the efficacy question, without treating an interpreter outcome as native proof.

## Next research decision

The bounded help-exposure observation is complete. Stop adding prerequisite
features unless an experiment hits a concrete blocker. Run an
unfamiliar rule repair or refactor using current Flow/2 and conventional F#.
Reuse existing broker/scoring machinery and a small independent oracle.

Use retained vocabulary, reset-rich vocabulary and a conventional typed project.
Match foundational types, implemented helper contracts, documentation and public
business rules; make the intended learned-helper availability difference explicit.
Make that difference explicit in the study plan, not as a hint naming the helper
in the actor prompt. The conventional baseline should receive equivalent useful abstractions and
ordinary strong typing, not an intentionally weak implementation. Record any
remaining topology or tooling differences rather than attributing their effects
to the language alone.

Start with two fresh actors per condition as a bounded replication, not a
statistical superiority claim. Freeze a plausible seeded defect and independent
expectations before dispatch. Do not name the repair target or prescribe a helper.
Score correct behavior, preserved unrelated behavior, erroneous test expectations,
actual helper reuse, successful publication and recovery separately. Preserve
failed attempts. Add a small predeclared mutation set to distinguish tests that
exercise paths from tests that reject wrong behavior.

If all arms again succeed, the result is another feasibility observation: choose
a harder or longer task sequence to obtain useful discrimination rather than
calling the tie a win. If retained vocabulary is ignored or causes confusion,
record that as evidence against the hypothesis. Do not resume the deferred 004
harness merely to produce another exploratory trial.

Approved construction, finite coverage and effect-count work has progressed.
Provider-state assertions, broader module/library dependency closure and scoped
dictionary overrides remain pending. These requirements remain part of the
roadmap; they need not all be implemented before the next efficacy comparison.
The overall PRD and research goal remain incomplete.

## Assessment provenance

The bounded source review and cited-file hashes are saved with the
[assessment evidence](evidence/124-efficacy-assessment/index.json). This is a
synthesis of explicitly separated studies plus report 123's new observation,
not a new pooled benchmark. It required no product build or native rerun.
