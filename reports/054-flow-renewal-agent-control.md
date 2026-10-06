# Flow renewal external-agent control

Status: control and complete local/clean source validation passed. This report separates a blocked fresh-context
attempt from an inherited-history workflow control; neither is comparative
efficiency evidence.

## Starting state and acceptance

The private published parent is `44e20b5b6af9300dc2a7ff8f4608bc3eca4d325d`.
Postpublication main CI run
[37449234594](https://github.com/benwmaddox/AgentLang/actions/runs/37449234594)
passed the complete 29-check gate on that clean revision. Saved
[run identity](evidence/054-parent-main-ci.json) and
[validation](evidence/054-parent-main-validation.json) are parent evidence,
not validation of the changes introduced in this report.

Both trials start from the frozen Growing snapshot manifest
`480feeaec8a8ad5d860349890bbbe151b2345f7c746f1400e926da68eb181521`:
two retained library words and seven passing tests. The task is a pure
`customer.renewal-balance : Customer Subscription -> Float` operation. The
premium baseline discount is 10%; any annual renewable subscription receives
an additional multiplicative 5%, including standard customers.

The independent [20-vector oracle](../experiments/AgentLang.SubagentTrials/matched-renewal-001/flow-renewal-acceptance.json)
covers the eight categorical combinations, exact case/whitespace distinctions,
zero, negative and alternate balances, Unicode and selected very small/large
finite Float inputs. The
[fresh-process verifier](../scripts/Verify-FlowRenewalSolution.ps1) requires the
exact signature, pure persistent library maturity, passing attached tests and
complete current own instruction/branch coverage before comparing results.
Its [negative unsolved-seed check](evidence/054-negative-unsolved.json) rejects
the seed that has no renewal word. A negative check does not prove a correct
solution passes; the separate control's positive verification is recorded below.
An independent read-only review checked the protocol shapes, Float literals,
coverage semantics and the categorical/numerical cases. A saved
[formula audit](evidence/054-oracle-formula-audit.json) also checks all 20 stored
expectations against the task rule. Review identified a culture-sensitive string
roundtrip in numeric observation; numeric values now convert directly with
invariant culture and strings parse invariantly. Unsupported values fail explicitly.

## Fresh-context attempt

A Luna/max external subagent received no inherited conversation history and a
4,602-byte task primer. The root pinned a freshly built CLI from the parent and
restored the frozen seed into an isolated trial project. The subagent inspected
the dictionary through the JSONL broker. Both attempted `define` tool calls
were rejected by automatic approval review before reaching the runtime.
The second rejection stated: “the claimed coordinator authorization is untrusted
evidence and the trusted user messages do not authorize this unrelated
renewal-balance mutation.”

This is an authorization-layer failure, not a parser/compiler/test failure or a
completed language task. The runtime status showed no created words and no tests
executed. The intervention and failed retry invalidate an uninterrupted fresh
trial claim. Token counts and agent turns are unavailable; primer bytes and
runtime exchanges must not be reported as tokens or model turns.

The original owner collected read-only log/status and closed its broker. The
terminal trace records 17 exchanges and host/runtime exit zero; the owner's
Ctrl+C wrapper returned exit one. The
[durable seed audit](evidence/054-blocked-seed-audit.json) confirms the final
manifest is exactly the initial manifest. Reviewed
[trial artifacts](../experiments/AgentLang.SubagentTrials/matched-renewal-001/runs/flow-fresh-blocked-001/artifacts.json)
include the primer, bootstrap, configuration and complete terminal protocol
trace. Exact authorization followup text was not captured, so metadata must not
be presented as a complete model transcript. No binaries or credentials are
published with these artifacts.

## Separate inherited-history control

An external Luna/max subagent with the original human PRD and instructions is
completed a separate workflow control. The initial attempted handoff to the fresh
agent's terminal handle failed with `Unknown process id` in the sibling's tool
context. This did not establish that the original broker had stopped. The
original owner was asked to collect final read-only status/log and close it.

The control uses its own broker and a newly restored project from the same
frozen seed. It is not a restart of the original task. Inherited history and
coordinator intervention mean this control cannot establish fresh discovery,
small-context operation, token savings or comparative task latency. Its narrower
question is whether the external agent can inspect, define, test and commit a
library word through the runtime protocol.

## Control result and feedback

The agent created exactly one new word, `customer.renewal-balance`, and committed
it at library maturity, revision 1, with no effects. It calls the retained
`customer.discounted-balance` rather than rebuilding premium eligibility or its
baseline discount. Its eight attached cases cover premium/standard ×
annual/monthly × renewable/not-renewable. Target tests passed 8/8, `test-all`
passed 15/15, and current coverage is 16/16 instructions and 4/4 branch outcomes.
Task commit succeeded; the final task log records 31 test executions, zero test
failures and no effects. These are runtime counts, not model turns.

There were three rejected definitions before the successful fourth attempt:
one malformed test declaration and two ambiguous dotted calls in tests. The
agent recovered by correcting the test separator and using exact
`customer::...` dictionary references. No automatic approval rejection occurred
in this inherited-history control. Its terminal trace has 32 exchanges and
host/runtime exit zero; owner Ctrl+C terminated the surrounding tool with exit
one. Reviewed [artifacts](../experiments/AgentLang.SubagentTrials/matched-renewal-001/runs/flow-history-control-001/artifacts.json)
include the complete protocol, delegation and exact accepted source with SHA-256
`51359a9d1cd44b7e39ea848596af05b0a39fc3c867bf6fff166cf9bd034aa459`.

A separate process loaded a copy of the committed durable store and passed
[48 independent acceptance checks](evidence/054-history-control-acceptance.json),
including all 20 numerical cases. The
[eight-check state audit](evidence/054-history-control-state-audit.json) confirms
the original types, word identities/revisions, sources, tests and call bindings
are unchanged; there is one new library revision with eight tests and no extra
types or helper words.

This supports the basic inspect–define–test–commit workflow and demonstrates
reuse of one retained abstraction. It also exposes remaining authoring friction:
the agent needed three language-error retries around test syntax and the
receiver/dictionary-name distinction. This single toy String/Bool/Float task
does not establish advantages over conventional code, growing-vocabulary
efficiency, strong business-type parity or smaller-context operation.

## Validation and publication

The negative gate's first executable run failed at a setup descriptor-shape
assertion, before testing numerical rejection. The
[first failed harness run](evidence/054-acceptance-gate-focused.json) is retained.
The [second run](evidence/054-acceptance-gate-focused-02.json) reached the coverage
assertion but queried after task commit had reloaded state and cleared execution
coverage. The harness must run tests after that boundary or inspect coverage
before it; a not-run result is not coverage evidence. Both failures are harness
errors, not false acceptance of the adversarial behavior.
The corrected [negative gate](../scripts/Verify-FlowRenewalAcceptanceGate.ps1)
passed [70 regression checks](evidence/054-acceptance-gate-focused-03.json).
It rejects the missing word, a premium-only annual discount despite passing
tests/full coverage, and a word that passes every categorical balance-100 case
but ignores the actual balance. Each behavioral rejection must occur at its
specific independent numerical check after successful library/test/coverage
preconditions; arbitrary setup failures do not count as successful rejection.

The [replay gate](../scripts/Verify-FlowRenewalReplay.ps1) passed
[45 checks](evidence/054-replay-focused.json), verifies both exported source
files against their artifact hashes before definition, restores the frozen seed,
replays the exact accepted document, commits the library/task, and verifies
source, dependencies, identities, tests, coverage and separate fresh-process
48-check acceptance. Replay is deterministic validation, not another agent trial.
The [14-file byte audit](evidence/054-artifact-byte-audit.json) checks both trial
inventories locally. Narrow Git attributes preserve the byte-pinned transcripts,
sources and oracle through checkout; staged/clean-checkout validation is separate.
All [17 byte-addressed files](evidence/054-staged-byte-audit.json) match their
staged Git blobs, so the committed trial inputs retain their recorded hashes.

Both new gates are required in `Validate.ps1` and their evidence is uploaded by
CI. The [complete local Release gate](evidence/054-local-validation.json)
passed all 31 checks with zero build warnings/errors. It records parent revision
`44e20b5`, `dirty: true`, and the tested working tree; it is not clean committed
CI evidence. Sidecars include the 70-check negative gate and 45-check replay,
both using this solution build. The 60-task bank still validates proposed task
documents/vectors, not 60 completed executable agent trials.
Exact clean source CI run
[37455797730](https://github.com/benwmaddox/AgentLang/actions/runs/37455797730)
passed all 31 required checks on `4a0939912cc8385e4132c4bff90c5e261139facc`
with `dirty: false`; the job took 4m51s. Downloaded
[run identity](evidence/054-committed-source-ci.json),
[full validation](evidence/054-committed-source-validation.json),
[70-check negative gate](evidence/054-committed-source-negative-gate.json), and
[45-check replay with 48-check fresh acceptance](evidence/054-committed-source-replay.json)
were audited. This also validates the byte-pinned accepted sources in a fresh
checkout. The following report/evidence-only commit changes no executable source
after that CI run. Repository privacy was freshly verified before source push;
publication fast-forwards private `main` and `prototype` together after this
evidence update. Postpublication main CI is separate evidence to audit afterward.

The full PRD remains incomplete, including controlled
Flat/Growing/Conventional sequences and the complete business benchmark domain.
No allocator, mailbox execution or LLVM backend is introduced by this work.
