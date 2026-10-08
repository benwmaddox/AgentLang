# Milestone reports

Each milestone report records delivered behavior, exact validation commands and results, observed feedback, and remaining limitations. Passing implementation checks does not establish the language's research hypothesis. Agent trials must identify the provider/model, modes, fixtures, acceptance oracles, usage source, and budget policy; scripted trials must be labeled as scripted.

Milestone publication includes updated reports in the same reviewed commit set as the implementation. After local validation, merge that commit set into the repository's `main` branch and push it; record the resulting revision and local validation evidence. Documentation-only checkpoints must identify implementation that remains in progress.

Run `./scripts/Validate.ps1` from PowerShell to build the solution and run every required acceptance runner and process verifier. Missing required projects or verifier scripts fail the gate. The command saves machine-readable evidence in `.agentlang/reports/validation.json` and exits with failure if any check fails. Use `-ReportPath PATH` to retain a particular run. Its `dirty` flag distinguishes a tested working tree from a committed revision; milestone prose should say which was tested.

Validation runs locally. The GitHub workflow is manual-only; milestone publication does not trigger a CI run. Human feedback and milestone conclusions belong in numbered Markdown reports here. Credentials must never appear in reports. Live provider traces can contain project information and should remain in the experiment's local output directory unless deliberately reviewed for publication.

Latest design research: [127 — Midori and Goose comparisons](127-memory-design-comparisons.md)
records external architectural references and hypotheses for memory experiments.
It is a documentation-only checkpoint; the owning-value implementation remains
in progress and no external benchmark claim is treated as an AgentLang result.

Latest native checkpoint: [126 — Standalone native mailbox dispatch](126-native-mailbox-dispatch.md)
moves bounded turns and arena ownership out of the managed host. The native
integration passes 353 checks, native conformance 476 assertions, and the full
local Release gate all 37 checks. Real async I/O, throughput, JIT and general
release certification remain pending; this is not new agent-efficacy evidence.

Latest comparison: [125 — Matched pair repair](125-matched-pair-repair.md) records
six fresh agents, each passing 12/12 frozen independent cases. Retained Flow and
F# actors reuse their existing helper; no reliability advantage is established.
One retained actor skips task.commit, so behavior and workflow completion are
reported separately.

Current research assessment: [101 — Approach efficacy review](101-approach-efficacy-review.md)
finds that discovery, typed composition and vocabulary reuse work, but comparative
reliability superiority remains unproven. [102 — Guided defect repair](102-guided-defect-repair-comparison.md)
records successful repairs in retained, reset and conventional F# conditions;
it does not establish blind defect discovery or a correctness advantage.
[109 — Location-unhinted repair](109-location-unhinted-repair.md) adds three fresh
actors: all repair the unnamed target to 54/54 independent cases. Vocabulary is
preserved in the language conditions; F# also completes two existing placeholders.
Fixture differences and one actor per condition preclude a superiority claim.

[111 — Current-syntax stateful comparison](111-current-syntax-stateful-comparison.md)
records passing Flow/2 and F# reminder implementations. A scripted extra-write
mutation passes the language's own tests and full coverage gate but fails the
independent effect checks, supporting explicit state/effect assertions alongside
coverage. This does not establish comparative superiority.

The structural correctness checkpoints are
[110 — Explicit arithmetic and domain contracts](110-structural-correctness.md)
and [112 — Closed domain states](112-closed-domain-states.md): checked ratios,
a validated bounded-rate library, and payload-free Flow/2 enums with exhaustive
matching and durable project definitions. Report 116 below adds finite-value
qualification for supported enum-bearing libraries; native enum execution remains pending.
[113 — Effect-count assertions](113-effect-count-assertions.md) adds optional
target-scoped provider counts to Flow/2 tests. Cross-field construction and
provider-state assertions remain pending. These are implementation checks, not new
agent-efficacy results.

[114 — Effect-assertion adoption](114-effect-assertion-adoption.md) records a
negative discovery result: one fresh actor repaired the reminder behavior but
did not adopt Flow/2 count assertions; its help requests received Flow/1 guidance.
Its seven tests
accepted the extra-write mutant. Behavior acceptance and feature adoption are
reported separately. [115 — Versioned-help adoption](115-versioned-help-adoption.md)
records a fresh actor using the same runtime with a corrected help primer: its
tests reject the restored write and block publication. One actor per condition
establishes bounded usability, not comparative superiority.

[116 - Finite library coverage](116-finite-library-coverage.md) adds observed
Bool/enum inputs, finite returns and atomic requalification; all 37 local Debug
checks pass. [117 - Finite-coverage adoption](117-finite-coverage-adoption.md)
records one fresh actor adding both missing Boolean combinations and honestly
keeping an unreachable-None function at project maturity. Fourteen independent
behavior cases pass; its tests reject both frozen Boolean mutants. A scripted
three-row control still accepts one mutant, and the actor adds one redundant
test. This supports bounded usability, not comparative reliability superiority.

[118 — Populated delivery contracts](118-populated-delivery-contract.md) adds a
typed interpreter `list.tail` and a separately loaded Flow/2 delivery extension
whose plan requires a real FIFO head. The focused business-transition run passed
8 groups and 5,382 assertions, preserving the 53-word / 31-type baseline; the
repository-wide Debug gate passes all 37 checks, and the separate LLVM suite
passes 444 assertions. This is not an agent or native
performance result. At that checkpoint, general cross-field validators and
provider-state assertions remained pending.

[119 — Whole-record construction invariants](119-record-construction-invariants.md)
adds optional pure record predicates carried through verified IR construction,
with focused Core/interpreter, persistence, library qualification and native
O0/O2 evidence. The separate validated lookup rejects mismatched identifiers
before and after reload. Focused business tests pass 5,479 assertions; native
conformance passes 455. The full Debug run passed 36/37 checks; the remaining
inspection-script repair passed its exact 31-assertion probe separately. The report is an
implementation checkpoint, not fresh-agent adoption; provider-state assertions,
broader module/library closure and test-local override rules remain open.

[120 — Record-invariant adoption](120-record-validator-adoption.md) records one
fresh Luna/max actor: all nine independent cases and five tests pass after
reload, but validator qualification remains incomplete. A separate coordinator
case proves the same predicate can qualify when the rejection test belongs to
the validator. This is behavioral success with an authoring/discovery gap,
not a comparative reliability result.

The latest ownership demonstration is [108 — Native handler mailbox suspension](108-native-mailbox-suspension.md):
120 checks across Core/O0/O2, with readable Flow/2 handlers and opaque retained
state across scratch reuse. Scheduling is still a .NET experiment host; no
production throughput or agent reliability advantage is claimed.

The native checkpoint is [107 — Typed state re-entry](107-native-state-reentry.md),
with 441 conformance assertions, 37 passing full-regression checks and separate
native safety/ABI evidence. It builds on
[106 — Record ownership](106-native-record-ownership.md) and
[105 — Refined scalars](105-native-refined-scalars.md) with scratch and retained
record storage. [104 — Arena/mailbox feasibility](104-native-arena-mailbox-feasibility.md)
remains a separate bounded memory experiment. These runtime checks establish no
agent-efficacy advantage.

Recent research reports include [064 — Repeated comparison](064-repeat-agent-purpose-review.md),
[067 — Selective retrieval](067-selective-retrieval-study.md),
[068 — Shared-defect debugging](068-shared-defect-agent-debugging.md),
[069 — Vocabulary refactoring](069-existing-vocabulary-agent-refactoring.md), and
[070 — Strong-type conversion](070-strong-type-agent-conversion.md).

Historical external-agent control: [054 — Flow renewal agent control](054-flow-renewal-agent-control.md)
completed an inherited-history inspect/define/test/library-commit workflow,
with 48 fresh-process acceptance checks and eight seed-preservation checks.
Its earlier fresh-context attempt was blocked before definition. Replay passed
45 checks and negative controls passed 70; both are required in the 31-check
Release gate, which passed locally with zero build warnings/errors and in clean
source CI run 37455797730 on `4a09399`. The following evidence-only publication
changes no executable source. No comparative efficiency claim is made.

Historical local checkpoint: [053 — Flow renewal fixtures](053-flow-renewal-fixtures.md)
passed the complete 29-check Release gate, including 129 Flow fixture checks and
26 frozen snapshot checks. It corrects a task/oracle mismatch and prepares
reproducible starting identities. Exact source CI and private publication are
tracked separately; the full PRD and controlled research outcomes remain incomplete.

Report 053 exact clean source CI passed all 29 checks on `63e80cb`
(run 37448525155), including fresh-checkout frozen snapshot validation. Saved
identity and validation artifacts distinguish this from the earlier local gate.

Report 052 exact clean source CI passed all 27 checks on `698ebcc`
(run 37442447178). Saved identity and validation artifacts distinguish this from
the earlier dirty-tree gate and from subsequent report-only publication.

- [055 — Early evaluation preparation](055-early-evaluation-preparation.md): five-task matched pilot contract, independent acceptance and wrong-solution controls; bounded typed fold integration. Comparative agent results remain pending.
- [056 — Early external-agent pilot](056-early-agent-pilot.md): first fresh language/conventional task pair accepted; protocol costs and friction recorded. Cumulative reuse sequence remains in progress.
