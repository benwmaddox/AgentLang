# 056 — Early external-agent pilot

Status: in progress. Source runtime is milestone 055, `e87931a`; copied binaries
are pinned in run artifacts. This is an exploratory apparatus sequence, not a
controlled efficiency result. Fresh external Luna/max agents receive no inherited
history and use bounded JSONL tools. No AI components were added to the runtime.

## First accepted pair

| Task 1 | Flat language | Conventional F# |
|---|---:|---:|
| Independent acceptance checks | 48 passing | 43 passing |
| Agent self-tests | 4 passing | 7 passing |
| Protocol exchanges | 16 | 6 |
| Runtime/protocol errors | 2 resolved | 0 |
| Response payload bytes | 20,063 | 2,552 |
| Request payload bytes | 1,869 | 2,230 |

Both implement exact case-sensitive premium classification. The language word
is pure, persistent library maturity, with 4/4 own instruction coverage. Its two
errors were a `search-type` request using `query` instead of `type`, and an
absolute-root reference used for a namespaced dictionary word. It recovered
through runtime diagnostics. The task goal remained empty because the agent's
`task.begin` request omitted the optional goal; this is not evidence of a lost
supplied goal. Type sources remain unchanged and only the requested word/revision
was added. Conventional self-tests cover case/whitespace and balance independence;
project configuration and the other unimplemented API entry points are unchanged.

This first pair supports the basic implementation workflow. It does not yet
support cheaper agent development: the language arm has appreciably higher
protocol/retrieval cost for this simple fresh task. Library publication adds
quality requirements, but coverage alone is not sufficient; separate adversarial
controls reject wrong implementations that pass their own tests and coverage.
Later matched tasks must demonstrate whether discoverable retained vocabulary
repays the early overhead. No comparative token/model-turn claim is possible.

## Conditions and limits

Flat resets to the same schema-only snapshot for every task. Growing begins from
that identical snapshot and retains only independently accepted work. Conventional
retains accepted source/tests. Fresh child launches initially hit the live-thread
cap twice; later launches succeeded after earlier agents finished. No inherited
agent was substituted for a fresh trial. No approval-layer rejection occurred in
the completed first pair. The test arms run concurrently to check the apparatus,
so elapsed time is not a controlled comparative metric. Broker traces record
protocol payloads, not all model context; exact tokens and model turns are null.
Tool-boundary instructions and broker path checks do not constitute OS isolation.
Independent acceptance is outside the agents' editable project roots.

The Growing first task and Conventional second task are underway. The five-task
sequence and its reuse/state audit remain incomplete. Larger business types,
allocator/mailbox research, LLVM work and optional language features are deferred
behind this experiment.

## Related publication evidence

Milestone 055 postpublication main CI run
[37467625232](https://github.com/benwmaddox/AgentLang/actions/runs/37467625232)
passed all 32 required checks on `2a1418ef0fc5117889c9935e8478c6f0be708456`,
with a clean checkout. Saved run identity and validation were audited. The present
pilot uses the same executable source, and acceptance is run with its pinned copy.
## Continuing sequence

Growing task 1 also passed all 48 independent checks with unchanged schema types
and one added library word. Its four tests cover all four own instructions. It
used 17 broker exchanges: two resolved runtime argument/syntax errors, plus one
rejected malformed shutdown control byte. Shutdown needed Ctrl-C after Ctrl-D
was echoed; the committed task and state were already complete and independently
accepted. Closing friction is reported separately from language execution.

A fresh Conventional task-2 agent discovered and reused `Customer.premium` in
`discountedBalance`. Its 15 self-tests (7 retained + 8 new) and 43 independent
acceptance checks pass; unrelated function definitions remain unchanged. This
confirms that the conventional comparison can also retain/discover useful APIs.
Its trace has 15 exchanges, one resolved inspect-argument error, and one malformed
shutdown exchange. Growing task 2 and Flat task 2 are now running on the same
pinned runtime. The Growing agent received one coordinator transport clarification
that the host has no startup banner; subsequent Flat launch instructions state
that explicitly. This apparatus clarification is not programming guidance and
is preserved as an intervention. Initial launch messages were not identical
beyond the matched task rules and intended protocol contracts, so these runs
must not be promoted to controlled token/turn or latency evidence.

Accepted trials and current metrics are retained under
[the pilot artifacts](../experiments/AgentLang.SubagentTrials/early-flow-001/runs/pilot-001/inventory.json).
## Complete task-2 comparison

| Task 2 | Flat | Growing | Conventional |
|---|---:|---:|---:|
| Independent acceptance checks passing | 48 | 48 | 43 |
| Broker exchanges including closure | 15 | 15 | 15 |
| Runtime errors resolved | 2 | 1 | 1 |
| Rejected shutdown input | 1 | 0 | 1 |
| Request payload bytes | 2,452 | 1,857 | 3,474 |
| Response payload bytes | 22,140 | 20,671 | 7,247 |
| Target own IR instructions covered | 11/11 | 9/9 | not measured |
| Target own branch outcomes covered | 2/2 | 2/2 | not measured |

Both language words pass three own tests. Growing reuses the earlier predicate;
Flat composes generated accessors/equality directly. Conventional also reuses
its predicate and preserves all earlier tests. Growing reduces authored request
bytes and two own IR instructions compared with Flat, but does not reduce
broker exchanges here. Its retrieved response bytes remain much larger than
Conventional. This is concrete abstraction reuse, with no demonstrated overall
cost advantage yet. Six completed trials are independently accepted; all three
arms' tasks 3–5 remain necessary to finish this apparatus sequence.

Growing task 3 hit model capacity after host startup and before any request.
It resumed in the same host session with unchanged state; this platform
interruption is separate from language/compiler failures. No model substitution
or hidden inherited solution was used. Runtime source remains frozen throughout.