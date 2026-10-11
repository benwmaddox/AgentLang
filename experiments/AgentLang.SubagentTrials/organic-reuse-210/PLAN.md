# 210 — Independent replication of organic vocabulary creation

Status: predeclared replication plan. No participant has been dispatched by
this preparation. The study uses the same creator task, report-163 seed,
acceptance model, and conditional held-out successor as trial 200. It is a
separate fresh-agent observation of that one opportunity, not a new task,
comparative study, or estimate beyond this task.

## Scope and protocol

Run one fresh Luna/max creator, then at most one fresh Luna/max successor.
Give the creator only the packet in `preparation/creator-packet.md`, a fresh
copy of `.agentlang/subscription-handoff-161/seeds/agentlang-project`, and its
assigned broker. Do not show either participant trial-200 results, reports, or
other agent context. The packet requests the same two fixed-term renewal
operations. The required public entry points and behavior are named; a shared
production helper remains optional, unnamed, and unprompted. Do not add hints
that would make factoring part of the task.

Score the creator against the frozen 66-case independent model. Require all
cases to execute and pass; the full inherited suite and all inherited examples
to pass; the requested roots to have their own tests and library maturity; and
all inherited definitions, types, tests, and examples to remain unchanged.
Helper eligibility is copied unchanged from 200: a helper must be absent from
the seed, introduced in the creator's source changes, be a production function
(not a test or observer), have a name and signature absent from the task, be
published as library, and be reached by both renewal entry points. Freeze the
full eligible set mechanically with the accepted creator project fingerprint.
An empty set is a valid endpoint and stops the chain; do not dispatch a
successor or claim reuse.

Only after the creator passes every gate and leaves a nonempty eligible set,
give a second fresh participant only the accepted creator project, the packet
in `preparation/successor-packet.md`, and its own broker. The held-out task is
the unchanged nine-case all-or-nothing `subscription.renew-batch` operation.
Do not reveal the creator prompt, trace, helper name, oracle, or report. Score
correct behavior, preservation, publication, discovery from the assigned
trace, and a production call path through any predeclared eligible helper as
separate outcomes. A correct inline implementation passes behavior but not
helper reuse.

Each participant is sequential, isolated by a fresh broker session, and capped
at 100 exchanges. Use the existing V2 host, termination audit, scorer, and
control runner; add no harness. Grant no external capabilities. Behavior
controls execute against the fresh current pinned Release CLI before dispatch;
run all six frozen variants (three creator and three successor) and require
all expected cases to execute, correct controls to pass, and each mutant to
fail its declared discrimination cases. Root owns the current build, five-file
runtime pin, source/control hash review, control receipts, and launch decision.

`oracle/model-checks.json` is a model-only audit export copied unchanged from
200; it is not a runtime-control receipt. The copied `unit_checks.py` checks it
against the frozen model without rewriting it. The trial's hidden cases remain
outside actor projects. Keep all 210 runtime and participant evidence under
`.agentlang/organic-reuse-210/`; preserve trial 200's plan, scorer, and archive.

## Existing validation and launch commands

Run the Python-only model/scorer checks and verify the exact pinned runtime and
host before controls or participants:

```powershell
python -B experiments/AgentLang.SubagentTrials/organic-reuse-210/oracle/unit_checks.py
pwsh -NoProfile -File scripts/Verify-SubagentTrialHostV2.ps1 `
  -CliDll .agentlang/organic-reuse-210/runtime/AgentLang.Cli.dll
python -B experiments/AgentLang.SubagentTrials/organic-reuse-210/oracle/run_preflight_controls.py `
  --cli .agentlang/organic-reuse-210/runtime/AgentLang.Cli.dll `
  --seed-project .agentlang/subscription-handoff-161/seeds/agentlang-project `
  --evidence-root .agentlang/organic-reuse-210/preflight/controls
```

After root accepts those receipts, start the creator with the frozen operation
allowlist and run the existing termination audit and independent scorer:

```powershell
pwsh -NoProfile -File scripts/Start-SubagentTrialHostV2.ps1 `
  -CliDll .agentlang/organic-reuse-210/runtime/AgentLang.Cli.dll `
  -ProjectPath .agentlang/organic-reuse-210/actors/creator `
  -TracePath .agentlang/organic-reuse-210/traces/creator.jsonl `
  -AllowedOperations 'help,words,source,tests,define,test,test-all,commit,storage.status,task.begin,task.status,task.commit,describe,search,eval,context,dependencies,callers,failed-tests,examples,effects,type-of,history,task.abort' `
  -Profile agentlang -ExchangeTimeoutMilliseconds 120000 -MaxExchanges 100
pwsh -NoProfile -File scripts/Audit-SubagentTrialTerminationV2.ps1 `
  -TracePath .agentlang/organic-reuse-210/traces/creator.jsonl
python -B experiments/AgentLang.SubagentTrials/organic-reuse-210/oracle/score_organic_reuse.py `
  --stage creator --actor-project .agentlang/organic-reuse-210/actors/creator `
  --seed-project .agentlang/subscription-handoff-161/seeds/agentlang-project `
  --cli .agentlang/organic-reuse-210/runtime/AgentLang.Cli.dll `
  --evidence-root .agentlang/organic-reuse-210/scoring/creator
```

Only if the creator score passes and its eligibility record is nonempty, freeze
that accepted project and record under `.agentlang/organic-reuse-210/accepted/`
and run the successor through a fresh host session:

```powershell
pwsh -NoProfile -File scripts/Start-SubagentTrialHostV2.ps1 `
  -CliDll .agentlang/organic-reuse-210/runtime/AgentLang.Cli.dll `
  -ProjectPath .agentlang/organic-reuse-210/actors/successor `
  -TracePath .agentlang/organic-reuse-210/traces/successor.jsonl `
  -AllowedOperations 'help,words,source,tests,define,test,test-all,commit,storage.status,task.begin,task.status,task.commit,describe,search,eval,context,dependencies,callers,failed-tests,examples,effects,type-of,history,task.abort' `
  -Profile agentlang -ExchangeTimeoutMilliseconds 120000 -MaxExchanges 100
pwsh -NoProfile -File scripts/Audit-SubagentTrialTerminationV2.ps1 `
  -TracePath .agentlang/organic-reuse-210/traces/successor.jsonl
python -B experiments/AgentLang.SubagentTrials/organic-reuse-210/oracle/score_organic_reuse.py `
  --stage successor --actor-project .agentlang/organic-reuse-210/actors/successor `
  --creator-project .agentlang/organic-reuse-210/accepted/creator/project `
  --eligible-helpers .agentlang/organic-reuse-210/accepted/creator/helper-eligibility.json `
  --trace .agentlang/organic-reuse-210/traces/successor.jsonl `
  --seed-project .agentlang/subscription-handoff-161/seeds/agentlang-project `
  --cli .agentlang/organic-reuse-210/runtime/AgentLang.Cli.dll `
  --evidence-root .agentlang/organic-reuse-210/scoring/successor
```

Do not dispatch participants before the current source/runtime/control pins
and preflight receipts are reviewed and accepted.
