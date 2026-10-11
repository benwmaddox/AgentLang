# 200 — Organic vocabulary creation and later reuse

Status: recommendation only. No participants or trial runs are included in this plan.

## Recommendation

Run one sequential, two-agent AgentLang chain. The first fresh agent receives the report 163 subscription seed and is asked to add two related renewal entry points. The public functions are task requirements; any shared production helper beneath them is left unnamed and optional. Transfer the exact accepted creator project, without its prompt or conversation, to a second fresh agent for a held-out batch-renewal change. Do not dispatch the second agent unless the first independently creates and qualifies a useful helper.

This is the shortest design that observes both an agent-selected abstraction and a later agent's correct use of it. It is a one-chain efficacy pilot, not a comparison against F# or a vocabulary-free condition. Its conclusion is whether this particular chain succeeded, with behavior and preservation scored independently.

## Why this closes a real gap

Report 199 calls for testing whether one agent's useful abstraction is discovered and correctly reused by a fresh later agent on a held-out task, using existing brokers and independent acceptance. Reports 196 and 197 evaluate maintenance of vocabulary supplied in the starting projects; report 197 adapts only the independent check for one participant's record representation. Those results do not show a later agent reusing vocabulary that the prior agent chose to create.

Report 163 is useful preparation and a scoring reference, but its `subscription.handoff` is not itself organic vocabulary for this question. Its producer prompt explicitly names the function and full signature, and the agent adds that requested public function. Its implementation is agent-authored, while the abstraction was task-prescribed. The report 163 baseline seed is suitable because it has tested `subscription.start` and `subscription.cancel` but no handoff operation. Reuse its seed and independent model, not its prescribed handoff as the claimed new abstraction.

Report 147 already attempted report 145's agent-authored rental output on a batch task. The Flow actors failed to discover the fold spelling, and they exchanged findings; report 147 therefore does not establish later-agent reuse. Do not repeat that exact task. Preflight the Flow/2 list operation needed by the new held-out batch task, and isolate each actor to its assigned broker. Report 166 also shows why passing behavior and library gates cannot stand in for preservation: one submission changed prior assertions. Keep source and evidence preservation as a separate hard endpoint.

## Bounded chain

Use the prepared AgentLang project from `.agentlang/subscription-handoff-161/seeds/agentlang-project` as the creator's initial state, after rechecking it with the eventual pinned Release CLI. It has no handoff implementation and report 163 records a passing 137-test baseline. Its historical runtime is not the trial runtime: rebuild and pin one current Release runtime after the active native Bool work is accepted, then revalidate the seed and frozen controls against that exact binary. “Clean” here means that every source, project seed, scorer/model, and runtime dependency relevant to the trial is committed or otherwise frozen and hash-pinned. It does not require an empty whole-checkout `git status`; preserve unrelated untracked user artifacts and do not clean them to prepare the trial.

**Creator, one fresh Luna/max participant.** Ask for two documented, pure library operations: `subscription.renew-monthly(store: Store, oldId: SubscriptionId, newId: SubscriptionId, at: Instant, expiry: Instant) -> Result<Store, BusinessError>` and the same signature for `subscription.renew-annual`. Each ends the old subscription at `at` and starts its replacement for the same customer and product with its fixed term (`monthly` or `annual`). Preserve established cancellation-then-start error precedence, return the existing error rather than a successful intermediate Store, and preserve every unrelated value and the caller's input. Require new tests and preservation of existing definitions and evidence. The two public operations and their behavior are task requirements; do not name a shared helper, ask the participant to factor the logic, or prescribe an internal signature.

A created helper qualifies as organic vocabulary only if it is absent from the inventoried seed, appears in the participant's source changes, is not a test/observer function, has a name and signature absent from the task, is published as library, and is reached by both new production entry points. The two named renewal operations are task-required and do not qualify. Before dispatch, freeze this eligibility rule and record the full qualifying-helper set mechanically from the accepted source/call graph; if several qualify, the successor scorer will count reuse of any member, not a post-hoc selected favorite. If no qualifying helper exists, record a valid no-helper creation endpoint and stop. Do not dispatch a successor unless the creator completes, passes independent behavior and preservation acceptance, and leaves a nonempty eligible helper set. A rejected creator likewise yields no successor and no reuse claim.

**Successor, one fresh Luna/max participant.** Give a new context the creator's independently accepted project snapshot and a held-out request to add an all-or-nothing batch plan-change operation. Its input is a list of records containing old ID, replacement ID, monthly/annual plan, and expiry, plus one common change instant. Empty input returns the original Store. Successful requests apply in input order. The first failing request returns its established error. As `Result<Store, BusinessError>` carries no Store in its error case, the test observes atomicity through the error tag/code plus unchanged caller input; a later failure must not be turned into a successful partial result. Preserve unrelated subscriptions, list order, all original source and attached tests/examples, and all creator behavior. Do not mention the creator helper's name or require its use. The held-out contract makes reuse observable while leaving the choice with the agent.

The successor sees only the frozen accepted project, public task and assigned broker. It receives no creator prompt, trace, notes, hidden cases or other agent access. The scorer should classify discovery from the broker trace and actual production call graph separately: source inspection alone is not reuse; require a production dependency path from the batch operation through at least one member of the pre-recorded qualifying-helper set. A correct inline implementation with no such path passes behavior but records no reuse.

## Frozen acceptance

Freeze both public task texts, both hidden case sets, source/runtime pins, exact initial project inventory, creator eligibility rule and controls before the creator starts. The successor input is necessarily frozen after creator acceptance; bind it by hash to the accepted project, and never edit that snapshot during transfer. Keep both actor sessions sequential, fresh, and under the existing 100-exchange cap. Do not disclose cases or score feedback during either session.

For the creator, adapt the report 163 independent cell model to run the 33 existing scenarios against both renewal operations (66 target observations). The model's `request.term` is a fixture input to `model.expected`, not an argument to either fixed-term renewal API: for each operation, deep-copy each case, overwrite `request.term` with its fixed constant (`monthly` or `annual`), and score the matching public operation. This also means the inherited `blank-term` row is no longer a blank-term test; with the fixed nonblank term its expected result is recomputed and should succeed when all other inputs are valid. Check complete Store projections, error codes/precedence, and input preservation. For executable controls, retain report 163's `start-before-cancel` ordering mutant and its `leak-intermediate` mutant: on `bad-period-precedes-overlap`, the correct outcome is an expiry error, while the leak mutant returns `ok` with the old subscription cancelled and no replacement. This wrong-`ok` partial Store is representable under `Result<Store, BusinessError>`; an `error` result cannot carry a Store, so do not specify an impossible error-with-Store control. A correct control must pass and both mutants must fail. Run every inherited test and example in a fresh process. Review all prior identities, revisions, function sources, tests, and examples byte-for-byte; allow additions for the two requested functions, the eligible helper, and new tests only.

For the successor, run all creator cases again, then score a compact held-out batch set: empty list, one valid item, two valid items, a later-item failure after an earlier valid item, a first-item failure, repeated/duplicate IDs, a conflict with an unrelated active subscription, and preservation of unrelated entities and order. The independent model must compute expected transitions and errors itself; it must not call participant code or trust participant-authored tests. Correct controls pass. At minimum, an executable atomicity mutant and an error-order mutant must fail the intended cases. Because the API returns `Result<Store, BusinessError>`, the atomicity mutant is an incorrect `ok` containing a partially applied Store after a later item should fail (for example, the first replacement is present although the batch's second item is invalid); do not model an `error` carrying a Store. Preserve all creator definitions and all old test/example bodies exactly; report behavior, qualifying reuse, local suite/library gates, source preservation, finalization, recovery and broker termination as distinct endpoints.

The no-helper stop rule is important. It prevents the coordinator from manufacturing an organic predecessor or silently changing the research question. A failed or unaccepted creator produces no later-agent reuse result.

## Existing files and commands

Reuse the report 163 seed at `.agentlang/subscription-handoff-161/seeds/agentlang-project`, its public API contracts in `experiments/AgentLang.SubagentTrials/subscription-handoff-161/PLAN.md`, and the independent model and cases in `experiments/AgentLang.SubagentTrials/subscription-handoff-161/oracle/model.py` and `cases.json`. Use `experiments/AgentLang.SubagentTrials/subscription-handoff-161/oracle/score_agentlang.py` and `experiments/AgentLang.SubagentTrials/subscription-handoff-maintenance-166/oracle/score_agentlang.py` as adapter and source-fingerprint references. Do not run those scorers in place for this trial: they write evidence into their historical `.agentlang` roots, and the 166 adapter expects a seven-argument dry-run version of `subscription.handoff`.

Add one small, trial-specific scorer under `experiments/AgentLang.SubagentTrials/organic-reuse-200/oracle/` that imports/copies the cell model and typed fixtures, accepts explicit `--cli`, `--actor-project`, and `--evidence-root` arguments, and writes only under `.agentlang/organic-reuse-200/`. This is the only new scoring code required; do not add a reusable study framework. Use the existing transport and termination tools:

```powershell
pwsh -NoProfile -File scripts/Verify-SubagentTrialHostV2.ps1 `
  -CliDll .agentlang/organic-reuse-200/runtime/AgentLang.Cli.dll

pwsh -NoProfile -File scripts/Start-SubagentTrialHostV2.ps1 `
  -CliDll .agentlang/organic-reuse-200/runtime/AgentLang.Cli.dll `
  -ProjectPath .agentlang/organic-reuse-200/actors/creator `
  -TracePath .agentlang/organic-reuse-200/traces/creator.jsonl `
  -AllowedOperations 'help,words,source,tests,define,test,test-all,commit,storage.status,task.begin,task.status,task.commit,describe,search,eval,context,dependencies,callers,failed-tests,examples,effects,type-of,history,task.abort' `
  -Profile agentlang -ExchangeTimeoutMilliseconds 120000 -MaxExchanges 100

pwsh -NoProfile -File scripts/Audit-SubagentTrialTerminationV2.ps1 `
  -TracePath .agentlang/organic-reuse-200/traces/creator.jsonl

python -B experiments/AgentLang.SubagentTrials/organic-reuse-200/oracle/score_organic_reuse.py `
  --stage creator --actor-project .agentlang/organic-reuse-200/actors/creator `
  --cli .agentlang/organic-reuse-200/runtime/AgentLang.Cli.dll `
  --evidence-root .agentlang/organic-reuse-200/scoring/creator
```

After the creator passes independent behavior and preservation acceptance with a nonempty eligible-helper set, freeze its accepted project at `.agentlang/organic-reuse-200/accepted/creator/project` and copy the accepted creator run's `helper-eligibility.json` to `.agentlang/organic-reuse-200/accepted/creator/helper-eligibility.json`. Then run the successor scorer with its assigned trace:

```powershell
python -B experiments/AgentLang.SubagentTrials/organic-reuse-200/oracle/score_organic_reuse.py `
  --stage successor --actor-project .agentlang/organic-reuse-200/actors/successor `
  --creator-project .agentlang/organic-reuse-200/accepted/creator/project `
  --eligible-helpers .agentlang/organic-reuse-200/accepted/creator/helper-eligibility.json `
  --trace .agentlang/organic-reuse-200/traces/successor.jsonl `
  --cli .agentlang/organic-reuse-200/runtime/AgentLang.Cli.dll `
  --evidence-root .agentlang/organic-reuse-200/scoring/successor
```

The accepted creator project and eligibility record must remain byte-for-byte unchanged during transfer; the scorer checks their fingerprint binding. If the trial host or runtime artifact changes, run the existing host verifier on the exact pinned DLL before dispatch.

## Blockers and evidence limits

Root accepted all 37 native198 fullRelease business-policy checks and committed/pushed them as `0da7c2761ca02bc3bb2d7e6dab22f8e83525c20d`. The accepted runtime-pin receipt compares the 37 relevant source inputs and all five frozen/current runtime hashes; no rebuild was needed because commit metadata alone changed. Root's frozen Release CLI came from a fresh `fullRelease2` build, and its preflight receipts report 137 inherited seed tests passed plus the exact Flow/2 fold help example passing define, tests, example, and eval. This preparation did not run the runtime. Root owns review and execution of the frozen controls; the pinned-CLI control result is recorded in `preparation/preflight-gaps.md`. Those control receipts and the final source/runtime/control hash comparison must be accepted before either participant starts. At participant freeze, compare the relevant source/dependency inputs and runtime hashes against the accepted pin; rerun or rebuild only if a relevant input changed or a gate fails. A commit-head or `sourceRevisionId` difference alone does not require another build. Report 147 identifies the fold-discovery issue: the exact Flow/2 fold form and its help response must pass the correct batch control before dispatch. If a preflight fails, keep the trial identity and acceptance rules fixed while resolving that blocker.

Two actors, one domain and one accepted chain cannot estimate how often agents create useful abstractions or later reuse them, compare reliability with F#, prove a causal benefit over a vocabulary-free start, or measure provider tokens/context. Broker exchanges and payload bytes are process evidence only. The experiment can establish a narrower result: whether one fresh creator chooses and publishes an unprompted shared abstraction for two correct flows, and whether one later fresh agent discovers and correctly reuses it on a harder held-out operation while retaining prior evidence.

## Sources

- `reports/199-efficacy-decision-after-reference-maintenance.md`, especially the open accumulation question and cautions on participant counts, fixed order, supplied vocabulary, collateral criteria, and unsupported token/context conclusions.
- `reports/196-typed-reference-maintenance-cohort-results.md` and `reports/197-typed-reference-record-supplement.md`, for the supplied-vocabulary distinction, representation-adapter separation, and prior test/example preservation findings.
- `reports/163-atomic-subscription-handoff.md` and `experiments/AgentLang.SubagentTrials/subscription-handoff-161/PLAN.md`, for predecessor seed, model and case reuse. In `oracle/model.py`, `case` at line 34 supplies the request shape; `expected` at lines 99-149 consumes `request.term`, and `start_error` at lines 118-121 implements `leak-intermediate` as an `ok` result with the cancelled intermediate Store. The fixed-term adapter must override that fixture field before calling the independent model. The exact producer prompt in `.agentlang/subscription-handoff-163/prompt-flow.md` is the provenance check showing that `subscription.handoff` itself was task-prescribed.
- `reports/147-agent-authored-batch-reuse.md`, for the fold-discovery mismatch and independence incident; `reports/166-handoff-signature-maintenance-agent-pair.md`, for test-preservation limitations.
- `docs/ROADMAP.md`, section “Validate reliability beyond easy composition” and its guidance to use held-out changes with collateral-regression opportunities and separate correctness from evidence preservation.
- `docs/SUBAGENT-TRIAL-HOST-V2.md` and `docs/VOCABULARY-RETENTION-TRIAL-PLAN.md`, for participant isolation, fresh project copies, explicit `host.close`, immutable input pins, and accepted-output transfer.
