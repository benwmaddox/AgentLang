# Milestone reports

Each milestone report records delivered behavior, exact validation commands and results, observed feedback, and remaining limitations. Passing implementation checks does not establish the language's research hypothesis. Agent trials must identify the provider/model, modes, fixtures, acceptance oracles, usage source, and budget policy; scripted trials must be labeled as scripted.

Run `./scripts/Validate.ps1` from PowerShell to build the solution and run all available acceptance runners. The command saves machine-readable evidence in `.agentlang/reports/validation.json` and exits with failure if any check fails. Use `-ReportPath PATH` to retain a particular run. Its `dirty` flag distinguishes a tested working tree from a committed revision; milestone prose should say which was tested.

CI runs the same command and uploads its JSON report. Human feedback and milestone conclusions belong in numbered Markdown reports here. Credentials must never appear in reports. Live provider traces can contain project information and should remain in the experiment's local output directory unless deliberately reviewed for publication.
