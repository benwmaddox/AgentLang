# Fresh-agent stateful reminder composition

2026-10-06. A fresh external Luna/max agent added a documented, tested library
word that queues an open invoice's virtual reminder once, reusing an existing
path helper. Independent acceptance passes 125 checks, including exact effect
counts, repeat calls, custom-marker preservation, snapshot restoration and
capability denial. This is one stateful feasibility result, without a matched
conventional comparison or evidence of lower model cost.

## Fixture and prelaunch checks

The [fixture](../experiments/AgentLang.SubagentTrials/stateful-flow-001/runs/stateful-001/fixture-setup.json)
is created through the ordinary Flow protocol by the
[preparation script](evidence/071-prepare-stateful-study.ps1). It contains
nominal String wrappers `InvoiceId` and `InvoiceStatus`, a record
`Invoice(id: InvoiceId, status: InvoiceStatus)`, and pure library word
`invoice.reminder-path`. The helper's two tests pass with 4/4 instructions and
no branches; its example passes too. These wrappers distinguish types but do
not validate GUIDs, status enums or safe path segments. This is not the full
business-fixture contract.

The public task requires exact status `open`, using the helper's relative path
`outbox/invoice-reminders/<id>`. A missing marker is written as `queued`; an
existing marker is read and returned unchanged. Every other status returns
`not-open` without provider operations. The provider is an in-memory virtual
filesystem. No host-file I/O, database or email delivery is implemented here.

Fresh source `cb1ebf6` builds with zero warnings/errors. CLI SHA-256 is
`86c5a021a8371f0bb40219e177eed537ffc577582c3f4a522f784e2e68ee7590`;
Core is `ee50ac05a8ddd338b34e3a44106aa6861870918ff99da1c7fa2dc8c2e97f54cc`.
The [prelaunch record](../experiments/AgentLang.SubagentTrials/stateful-flow-001/runs/stateful-001/prelaunch.json)
pins source/runtime/host, starting inventory, prompt and independent oracle
before actor launch. The actor has no inherited turns and is instructed to use
only its isolated project protocol. That is an instruction boundary, not hard
filesystem isolation. The host grants only `fs.read` and `fs.write`, uses a fixed
clock, allows 100 exchanges with a 120-second exchange deadline, and enables no
cumulative inspection-response cap.

The coordinator-created
[correct control](../experiments/AgentLang.SubagentTrials/stateful-flow-001/runs/stateful-001/acceptance-preflight.json)
passes the complete frozen oracle before launch. The
[untouched seed](../experiments/AgentLang.SubagentTrials/stateful-flow-001/runs/stateful-001/no-op-acceptance.json)
fails the missing-persisted-word gate as intended. Two prelaunch issues remain
in the evidence:

- The first control used an absolute-looking `/outbox/...` path. Word tests and
  library coverage passed, but snapshot save rejected that path: durable virtual
  state requires relative slash-separated paths without empty, `.` or `..`
  segments. The [failed control and original seed](../experiments/AgentLang.SubagentTrials/stateful-flow-001/runs/preflight-absolute-01/acceptance-preflight.json)
  are retained. A new relative-path fixture is prepared before actor inputs are
  frozen. Runtime behavior is unchanged.
- A later [verifier failure](../experiments/AgentLang.SubagentTrials/stateful-flow-001/runs/stateful-001/acceptance-preflight-01.json)
  misclassified the empty JSON effect map because of PowerShell property
  enumeration. Recorded runtime effects were correctly empty. The verifier was
  corrected before freeze and rerun successfully.

Neither issue is an agent failure. The path mismatch is a real lifecycle
constraint: own-word coverage does not replace integration checks or make
provider persistence automatic. Early path validation or a checked path type
is a follow-up; the current primitive still accepts broader virtual-map keys.

## Verified outcome

The agent publishes exactly one new library word, revision 1. Its implementation
reads the typed status, checks exact `open`, derives the key through the retained
helper, and confines all filesystem operations to that branch. Source review
and compiled dependencies confirm that the helper's returned path is used by
the existence/read/write operations. The original helper identity, revisions,
types, tests/examples and immutable source/history files are unchanged.

All four own tests pass: new open, existing queued, existing different marker,
and a case-sensitive non-open status. Coverage is 22/22 instructions and 4/4
branch outcomes. Documentation and one first-class example survive reload.
All six project tests pass in a fresh process. Separate
[fresh-process example checks](../experiments/AgentLang.SubagentTrials/stateful-flow-001/runs/stateful-001/example-verification.json)
execute both retained examples successfully on a disposable copy.

The frozen [independent verifier](evidence/071-verify-stateful-trial.ps1) and
[acceptance result](../experiments/AgentLang.SubagentTrials/stateful-flow-001/runs/stateful-001/acceptance.json)
check these provider behaviors independently of the agent's tests:

| Invocation | Result/state | `fs.read` | `fs.write` |
| --- | --- | ---: | ---: |
| First open invoice | `queued`, marker created | 1 | 1 |
| Same invoice again | Existing `queued`, unchanged | 2 | 0 |
| Different open ID | Independent marker created | 1 | 1 |
| Existing custom marker | Exact text retained | 2 | 0 |
| Paid, cancelled or `OPEN` | `not-open`, no marker | 0 | 0 |
| Repeat after snapshot load in a fresh process | Restored `queued`, unchanged | 2 | 0 |

These counts refer to the target invocation; separate observer reads are
excluded. Provider persistence is demonstrated through named snapshot save/load,
not inferred from ordinary source reload. A process with no granted filesystem
capabilities receives `CAPABILITY_DENIED` with no recorded provider operations.
Passing plain String to the Invoice parameter is rejected with
`FLOW_ARGUMENT_TYPE`. Acceptance uses disposable copies and verifies unchanged
seed/final file inventories.

## Trace and implications

The [trace audit](../experiments/AgentLang.SubagentTrials/stateful-flow-001/runs/stateful-001/trace-audit.json)
verifies 22 exchanges, 2,981 request payload bytes and 16,257 selected response
payload bytes. It checks wire/decoded JSON equality, hashes/framing, provider
policy, allowlist, prelaunch ordering and unchanged oracle/prompt/runtime pins.
One `search-type` request omits the required `type` field and receives
`DISCOVERY_INVALID_ARGUMENT`; the agent recovers through `context` queries.
No coordinator repair hints are supplied. Host session 77850 ends with Ctrl+C
after completion; host and runtime exit zero with no added JSONL request.

Schema 1 records selected responses before stdout flush. It does not prove
delivery or model consumption. Exact model tokens, turns, windows and controlled
duration are unavailable. Neither this single successful task nor its branch
coverage establishes a general efficiency or correctness advantage.

The observed outcome extends feasibility beyond pure calculations: a fresh agent
can discover a retained abstraction, compose checked effects, cover branching
code, and publish inspectable behavior with documentation and an example.
Independent state and lifecycle oracles remain necessary. Broader provider
coverage, path-scoped policies and the full strong business fixture remain open.

Clean [main CI](evidence/070-main-ci.json) passed all 32 checks at `cb1ebf6`;
the [artifact](evidence/070-main-validation.json) records `dirty: false`. This
milestone changes experiment evidence and documentation, with no runtime,
compiler or host behavior change. Stale fold prerequisites in the business/suite
plans are corrected to reflect the existing validated implementation.

Next priority is a matched conventional stateful control and broader executable
business tasks, keeping prompts/fixtures and acceptance equivalent. The full
60-task suite, controlled cost/context results and complete PRD audit remain
required. LLVM, arenas and mailbox research stay deferred; the goal stays active.
