# 063 — Repeated external-agent comparison: renewal composition

Status: all three Task 4 trials were accepted on 2026-10-06. The sequence has
12 accepted trials out of 15; the final three Task 5 trials are pending. Growing
Task 5 has launched, but its results are not included here.

Task 4 composes premium pricing with annual-renewal eligibility: premium
customers receive the existing 10% discount, and any annual renewable
subscription receives an additional 5% discount, including standard
customers. The same frozen `dc18d6e` apparatus, schema, public tasks, and
independent 20-vector oracle were used. The Conventional and Growing arms began
with three accepted helpers and their tests; Flat began with the schema only.

## Task 4 results

Each arm passed its independent 20-vector acceptance. Across the three arms,
the inventory records 60 arm-vector evaluations, 139 independent checks, 100
wire/provenance checks, and 88 inventoried files. The [task inventory](../experiments/AgentLang.SubagentTrials/early-flow-002/runs/repeat-001/task-4-inventory.json)
contains the per-file hashes and trial references.

| Arm | Independent acceptance | Exchanges | Request / response payload bytes | Runtime errors |
| --- | ---: | ---: | ---: | --- |
| Conventional | 43 checks; 20 vectors | 14 | 8,428 / 13,631 | 2 intermediate `HASH_INVALID` patch responses |
| Growing | 48 checks; 20 vectors | 19 | 2,899 / 16,167 | 0 |
| Flat | 48 checks; 20 vectors | 14 | 2,347 / 10,178 | 0 |

Conventional added `Customer.renewalBalance` by calling the existing
`Customer.discountedBalance`, then applying the additional 0.95 factor when
`Subscription.annualRenewable` is true. This composes to 85.5 for a premium
annual renewal and 95 for a standard annual renewal. The F# module order now
places `Subscription` before `Customer` so the latter can resolve the helper.
The source audit records 10 new self-test cases, retains all 18 previous cases,
and reports 28 own cases total. The two intermediate `HASH_INVALID` patch
responses were followed by a successful final validation and independent
acceptance. See the [final source](../experiments/AgentLang.SubagentTrials/early-flow-002/runs/repeat-001/conventional-task-4/final-project/Domain.fs),
[metrics](../experiments/AgentLang.SubagentTrials/early-flow-002/runs/repeat-001/conventional-task-4/metrics.json),
and [acceptance record](../experiments/AgentLang.SubagentTrials/early-flow-002/runs/repeat-001/conventional-task-4/acceptance.json).

Growing added `customer.renewal-balance`, directly reusing
`customer.discounted-balance` and `subscription.annual-renewable?`. Its eight
new tests passed with 10/10 instructions and 2/2 branch outcomes covered. The
pre-task and final manifests show the prior three words retain the same IDs,
revision 1 definitions, and test hashes; the `Customer` and `Subscription`
type-definition hashes are also unchanged. The final manifest adds the new
word at revision 1 with eight attached tests. Compare the [pre-task manifest](../experiments/AgentLang.SubagentTrials/early-flow-002/runs/repeat-001/growing-task-4/final-project/.agentlang/store/manifests/b8d42474ecf2d2c8ad96b58440ff334048977381024a9db028608ef0bc2da926.json)
with the [final manifest](../experiments/AgentLang.SubagentTrials/early-flow-002/runs/repeat-001/growing-task-4/final-project/.agentlang/store/manifests/87321af4340ce2192883e3e26b944f86e2b376cdcf4860fa9ca017a5243e29a0.json),
and see the [final dictionary](../experiments/AgentLang.SubagentTrials/early-flow-002/runs/repeat-001/growing-task-4/final-project/dictionary.agent)
and [metrics](../experiments/AgentLang.SubagentTrials/early-flow-002/runs/repeat-001/growing-task-4/metrics.json).

Flat implemented the composed policy in one persistent word and added six
tests. All six passed with 25/25 instructions and 6/6 branch outcomes covered;
the two schema type definitions were preserved. Its independent acceptance
record reports 48 checks. See the [final dictionary](../experiments/AgentLang.SubagentTrials/early-flow-002/runs/repeat-001/flat-task-4/final-project/dictionary.agent),
[metrics](../experiments/AgentLang.SubagentTrials/early-flow-002/runs/repeat-001/flat-task-4/metrics.json),
and [acceptance record](../experiments/AgentLang.SubagentTrials/early-flow-002/runs/repeat-001/flat-task-4/acceptance.json).

For this target, Growing covered a 10-instruction definition while Flat covered
25 instructions. Conventional also reused its prior pricing and eligibility
helpers, so reuse is not unique to the Growing arm. Growing used 19 exchanges
and 16,167 response payload bytes, compared with Flat's 14 exchanges and 10,178
response bytes; request payloads were 2,899 and 2,347 bytes respectively.
These measurements show no lower interface use for Growing on this task, and
the instruction counts describe this definition's coverage rather than
transitive execution or semantic benefit.

## Host verifier follow-up

Hosted [CI run 37507670551](https://github.com/benwmaddox/AgentLang/actions/runs/37507670551)
at clean `main` revision `42a9c6e` failed the `subagent-trial-host` check: 31 of 32 full-validation checks
passed, and the first `task.begin` exchange exceeded the verifier's 2,000 ms
test deadline at 2,018.8 ms. The trace recorded an uncertain 66-byte request
delivery, no confirmed complete line, no runtime response bytes, and exit 124.
That evidence does not show whether the request executed, so it was not
automatically replayed. The same focused check passed all 17 checks locally at
the original 2,000 ms setting; the delay source therefore remains unconfirmed.
See the [CI summary](evidence/062-main-failed-ci.json), [failed host evidence](evidence/062-main-failed-host.json),
[full CI validation record](evidence/062-main-failed-validation.json), [original local reproduction](evidence/063-host-reproduction.json),
and [timeout review](evidence/063-host-timeout-review.json).

The verifier now gives only the real-runtime language write and reload happy
paths a 15,000 ms exchange allowance, with 90,000 ms and 60,000 ms outer
budgets for their five and three requests. Helper defaults and the host's
production settings are unchanged. The explicit partial-response 4,000 ms and
no-read 500 ms deadlines, no-retry/recovery checks, process cleanup checks, and
byte-limit assertions remain unchanged. The [focused fixed host record](evidence/063-local-validation.subagent-host.json)
passed all 17 checks. The [full Release validation](evidence/063-local-validation.json)
passed all 32 checks with a zero-warning, zero-error build. That run used a
dirty working tree at `42a9c6e`; clean-source CI after this verifier edit has
not yet been observed.

## Measurement limits

Exchange and payload-byte counts are protocol proxies, not model tokens or
turns. Exact model usage and controlled whole-task duration are unavailable;
no overall model-cost or native-runtime benefit claim follows from these data.
Independent behavioral acceptance is separate from each arm's own
publication/self-test policy. Instruction and branch coverage show exercised
paths, not semantic correctness or reduced transitive execution. Conventional
F# validation ran through the broker's hash-copy and whole-file-read interface,
so these observations do not establish behavior with unrestricted repository
tools.

## Evidence

The [repeat artifacts](../experiments/AgentLang.SubagentTrials/early-flow-002/runs/repeat-001/)
retain intended prelaunch prompts, starting-state hashes, source and executable
pins, raw exchanges, independent acceptance, metrics, and final source/storage.
Provider-internal prompt bytes and exact model usage remain unavailable.
Coordinator cleanup stopped existing sessions without additional JSONL
requests. This report does not alter the frozen prompts, trial outputs, or
runtime semantics.
