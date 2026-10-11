# 211 — Held-out reuse of task-required vocabulary

## Scope and protocol

This is a separate held-out reuse observation using the accepted creator project
from organic-reuse-210. One fresh Luna/max developer receives that project and
the byte-identical `preparation/successor-packet.md`, then implements the same
nine-case `subscription.renew-batch` operation through the existing broker,
with a 100-exchange limit. The packet names the batch operation and its types,
but does not name the two existing renewal functions. No hints about target
names or reuse are given.

The predeclared endpoint is production reuse of the accepted creator's
task-required library functions `subscription.renew-monthly` and
`subscription.renew-annual`. Before scoring, the adapter binds those exact
names to the accepted project's pinned fingerprint, current source declaration,
signature, library maturity, and own tests. They must be absent from the report-
163 seed. This tests discovery and production-path reuse of existing vocabulary;
it does not test spontaneous helper creation and supports no comparative or
benefit claim.

After the participant stops, score the final saved project in two independent
copies with the existing 210 model and scorer: all 66 creator cases, then all
nine held-out batch cases. Require the inherited suite and examples, roots and
target functions to remain library vocabulary with their own tests, and
preservation of the seed and accepted creator project. For the vocabulary reuse
endpoint, require both exact names in successful trace inspection responses and
a current definition-call path from `subscription.renew-batch` to each target;
test-only calls do not count. Report behavior, suite/examples, library/tests,
preservation, discovery, and production paths as separate outcomes.

The adapter's raw scorer outputs use 210's helper-oriented field names only as
an internal compatibility format. The public 211 summary labels the endpoint
as task-required vocabulary reuse and makes no claim about optional helpers.
All evidence stays under `.agentlang/public-vocabulary-reuse-211/`. Root owns
the accepted snapshot, fresh build and runtime/source pins, preflight controls,
broker launch, and evidence review. Grant no external capabilities.

## Validation and scoring command

Python-only checks:

```powershell
python -B experiments/AgentLang.SubagentTrials/organic-reuse-210/oracle/unit_checks.py
python -B experiments/AgentLang.SubagentTrials/public-vocabulary-reuse-211/oracle/unit_checks.py
```

After root accepts the snapshot/runtime/control evidence and launches the
participant, score the saved project and trace:

```powershell
python -B experiments/AgentLang.SubagentTrials/public-vocabulary-reuse-211/oracle/score_public_vocabulary_reuse.py `
  --cli .agentlang/public-vocabulary-reuse-211/runtime/AgentLang.Cli.dll `
  --actor-project .agentlang/public-vocabulary-reuse-211/actors/developer `
  --creator-project .agentlang/public-vocabulary-reuse-211/accepted/creator/project `
  --targets .agentlang/public-vocabulary-reuse-211/accepted/creator/reuse-targets.json `
  --trace .agentlang/public-vocabulary-reuse-211/traces/developer.jsonl `
  --seed-project .agentlang/subscription-handoff-161/seeds/agentlang-project `
  --evidence-root .agentlang/public-vocabulary-reuse-211/scoring
```

The scorer launches only the pinned CLI against isolated saved copies for
evaluation. It does not launch or contact the participant.
