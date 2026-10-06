# Early matched agent evaluation

Status: prioritized pilot contract, 2026-10-06; no comparative results yet.
User direction is to take the shortest remaining path to reviewing agent
behavior, ahead of completing other prototype features.

## Boundary

Use the existing Customer(kind:String,balance:Float) and
Subscription(term:String,renewable:Bool) fixture. This is a small workflow
evaluation, not the complete strong business-type benchmark. No arena, mailbox,
LLVM, new business provider or full-domain implementation is a prerequisite.
The in-flight fold implementation is independently validated; pilot tasks below
do not depend on it.

All arms start with schemas only, with no authored business-policy helpers.
Existing Growing renewal snapshots contain two helpers and therefore cannot
serve as the original baseline for this cumulative comparison. Language arms
can share the frozen Flat schema baseline. Prepare an equivalent conventional
schema-only fixture before trials; its existing premium/discount helpers must
not leak into the initial baseline.

## Five tasks

Each agent receives the complete business rule relevant to its requested output.
Prior words are discovered rather than enumerated in its prompt. Exact String
comparisons remain case-sensitive; there is no trimming or normalization.

1. `customer.premium? : Customer -> Bool`: kind equals "premium".
2. `customer.discounted-balance : Customer -> Float`: premium customers receive
   a multiplicative 10% discount; other balances remain unchanged.
3. `subscription.annual-renewable? : Subscription -> Bool`: term equals "annual"
   and renewable is true.
4. `customer.renewal-balance : Customer Subscription -> Float`: apply the premium
   discount, then an additional multiplicative 5% discount to every annual,
   renewable subscription, including standard customers.
5. `customer.renewal-savings : Customer Subscription -> Float`: original balance
   minus that renewal balance, with the complete task-4 policy included.

Conventional functions have equivalent typed inputs/outputs. The names are
required acceptance entry points, not restrictions against reusable helpers.
Target words and any retained reusable helpers use the current library gate.
Agents add their own tests; hidden acceptance is outside their editing surface.

## Retention and isolation

- Flat: restore the same schema-only baseline for every task. Any policy needed
  by the current task must be recreated; accepted user words do not carry over.
- Growing: retain each independently accepted result for the next task. Start
  from the same schema-only baseline as Flat.
- Conventional: retain independently accepted F# changes across tasks, starting
  from the equivalent schema-only fixture and normal file-tool interface.

Use external Luna/max subagents with identical model/settings. Prefer a fresh
agent for each task to test discovery across agents. Inherited-history trials
are separately labeled controls and never substituted for fresh trials. Preserve
blocked attempts and coordinator interventions. A failed task does not seed
later tasks with unaccepted code; record the failure and predeclare whether the
remaining sequence stops or continues from the last accepted state.

Begin with one sequence per arm to check the apparatus, then repeat the three
arms with rotated order before making a comparative judgment. These five tasks
vary in difficulty; a raw downward task-cost trend is not evidence of growing
vocabulary value. Compare the same tasks across arms and repetitions.

## Acceptance and observations

Reuse the existing independent renewal vectors for task 4, with new independent
expected outputs for tasks 1–3 and 5. Include zero/negative/alternate balances,
case/whitespace distinctions and all kind/term/renewable combinations. Check
that every arm implements the same rule and that unsolved and deliberately
wrong implementations fail. Keep public agent tests separate from the oracle.

Pin runtime binaries/source, fixture hashes, prompts, tool contract and starting
state. Record protocol interactions, final state, independent acceptance,
attached tests, language errors/retries, actual reuse and coordinator help.
Runtime/tool interactions and context bytes are available proxies; they are
not model turns or tokens. Exact model usage stays unavailable unless provided
by the subagent interface. Document authorization-layer blocks separately from
language failures. Trial preparation and deterministic replay are not agent runs.

## Review checkpoint

The first report answers: can agents discover useful capabilities, create and
reuse tested abstractions, recover from diagnostics, and complete matched tasks
correctly with tolerable interaction cost? Report unfavorable outcomes too.
One apparatus sequence is exploratory; repetitions support a preliminary
purpose review, not the complete PRD success criteria or context-budget study.
Use the results to choose the next justified implementation work.

## Pilot execution decision

After a failed or blocked task, continue the sequence from its last independently
accepted state. Do not retain failed changes. The conventional schema-only seed
is experiments/AgentLang.SubagentTrials/early-flow-001/conventional/EarlyPilot.fsproj.
Before each fresh subagent, record its selected public task, primer hashes,
starting-state hash, host allowlist, binary hash and trace path. Use the existing
JSONL trial host for language operations; coordinator acceptance runs separately.
Audit every retained helper, task completion, and state preservation in addition
to the target acceptance. Run conventional agent-authored tests separately.
