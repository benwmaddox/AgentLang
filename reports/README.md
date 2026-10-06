# Milestone reports

Each milestone report records delivered behavior, exact validation commands and results, observed feedback, and remaining limitations. Passing implementation checks does not establish the language's research hypothesis. Agent trials must identify the provider/model, modes, fixtures, acceptance oracles, usage source, and budget policy; scripted trials must be labeled as scripted.

Milestone publication includes updated reports in the same reviewed commit set as the implementation. After local validation, merge that commit set into the private repository's `main` branch and push it; record the resulting revision and CI evidence. Documentation-only checkpoints must identify implementation that remains in progress.

Run `./scripts/Validate.ps1` from PowerShell to build the solution and run every required acceptance runner and process verifier. Missing required projects or verifier scripts fail the gate. The command saves machine-readable evidence in `.agentlang/reports/validation.json` and exits with failure if any check fails. Use `-ReportPath PATH` to retain a particular run. Its `dirty` flag distinguishes a tested working tree from a committed revision; milestone prose should say which was tested.

CI runs the same command and uploads its JSON report. Human feedback and milestone conclusions belong in numbered Markdown reports here. Credentials must never appear in reports. Live provider traces can contain project information and should remain in the experiment's local output directory unless deliberately reviewed for publication.

Latest local checkpoint: [052 — default Flow authoring](052-default-flow-authoring.md)
passed the complete 27-check Release gate. Exact source CI and private publication
are recorded in that report separately; the full PRD and controlled research
outcomes remain incomplete.

Report 052 exact clean source CI passed all 27 checks on `698ebcc`
(run 37442447178). Saved identity and validation artifacts distinguish this from
the earlier dirty-tree gate and from subsequent report-only publication.
