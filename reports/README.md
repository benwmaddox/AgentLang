# Milestone reports

Each milestone report records delivered behavior, exact validation commands and results, observed feedback, and remaining limitations. Passing implementation checks does not establish the language's research hypothesis. Agent trials must identify the provider/model, modes, fixtures, acceptance oracles, usage source, and budget policy; scripted trials must be labeled as scripted.

Milestone publication includes updated reports in the same reviewed commit set as the implementation. After local validation, merge that commit set into the repository's `main` branch and push it; record the resulting revision and local validation evidence. Documentation-only checkpoints must identify implementation that remains in progress.

Run `./scripts/Validate.ps1` from PowerShell to build the solution and run every required acceptance runner and process verifier. Missing required projects or verifier scripts fail the gate. The command saves machine-readable evidence in `.agentlang/reports/validation.json` and exits with failure if any check fails. Use `-ReportPath PATH` to retain a particular run. Its `dirty` flag distinguishes a tested working tree from a committed revision; milestone prose should say which was tested.

Validation runs locally. The GitHub workflow is manual-only; milestone publication does not trigger a CI run. Human feedback and milestone conclusions belong in numbered Markdown reports here. Credentials must never appear in reports. Live provider traces can contain project information and should remain in the experiment's local output directory unless deliberately reviewed for publication.

Current research assessment: [101 — Approach efficacy review](101-approach-efficacy-review.md)
finds that discovery, typed composition and vocabulary reuse work, but comparative
reliability superiority remains unproven. [102 — Guided defect repair](102-guided-defect-repair-comparison.md)
records successful repairs in retained, reset and conventional F# conditions;
it does not establish blind defect discovery or a correctness advantage.
[109 — Location-unhinted repair](109-location-unhinted-repair.md) adds three fresh
actors: all repair the unnamed target to 54/54 independent cases. Vocabulary is
preserved in the language conditions; F# also completes two existing placeholders.
Fixture differences and one actor per condition preclude a superiority claim.

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
