# Milestone reports

Each milestone report records delivered behavior, exact validation commands and results, observed feedback, and remaining limitations. Passing implementation checks does not establish the language's research hypothesis. Agent trials must identify the provider/model, modes, fixtures, acceptance oracles, usage source, and budget policy; scripted trials must be labeled as scripted.

Milestone publication includes updated reports in the same reviewed commit set as the implementation. After local validation, merge that commit set into the private repository's `main` branch and push it; record the resulting revision and CI evidence. Documentation-only checkpoints must identify implementation that remains in progress.

Run `./scripts/Validate.ps1` from PowerShell to build the solution and run every required acceptance runner and process verifier. Missing required projects or verifier scripts fail the gate. The command saves machine-readable evidence in `.agentlang/reports/validation.json` and exits with failure if any check fails. Use `-ReportPath PATH` to retain a particular run. Its `dirty` flag distinguishes a tested working tree from a committed revision; milestone prose should say which was tested.

CI runs the same command and uploads its JSON report. Human feedback and milestone conclusions belong in numbered Markdown reports here. Credentials must never appear in reports. Live provider traces can contain project information and should remain in the experiment's local output directory unless deliberately reviewed for publication.

Latest local checkpoint: [053 — Flow renewal fixtures](053-flow-renewal-fixtures.md)
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
