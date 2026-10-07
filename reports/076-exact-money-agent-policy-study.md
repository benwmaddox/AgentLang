# Exact-Money policy agent study

Nine fresh external Luna/max coding agents completed classification (S01),
discount basis points (S06), and exact discounted Money (S07) in three modes.
Seven trials passed full independent acceptance. Growing and Conventional
passed all three tasks; Flat passed classification and failed the later two
on missing documentation metadata. Failed trials remain frozen without repair.

| Mode | S01 calls / diagnostics | S06 calls / diagnostics | S07 calls / diagnostics | Accepted |
| --- | --- | --- | --- | --- |
| Growing | 32 / 5 | 14 / 0 | 47 / 9 | 3/3 |
| Flat | 26 / 4 | 48 / 7 | 48 / 8 | 1/3 |
| Conventional F# | 17 / 2 | 18 / 2 | 24 / 4 | 3/3 |

Calls count broker exchanges; diagnostics count failed protocol responses.
Test failures are recorded separately. These are exploratory observations,
not controlled token or latency measurements. The [results JSON](../experiments/AgentLang.SubagentTrials/business-policy-001/results.json)
records payload bytes, mutation requests, test batches, reuse and archive hashes.
The [run folders](../experiments/AgentLang.SubagentTrials/business-policy-001/runs)
preserve public prompts, prelaunch pins, starting and final projects, raw traces,
trace audits and independent acceptance for each actor.

Growing starts with 53 supplied business words, 31 types, 151 tests and 44
examples, then retains accepted task words. Flat resets to six Customer schema
types and no authored words or tests for each task. Conventional retains accepted
F# policy functions using existing nominal Business scalar types. It exposes raw
Customer.Kind rather than the reference constructor's trimmed kind. Each actor
was fresh, without inherited turns or coordinator repair. Flat S07 ran from its
independent reset seed despite S06 failing metadata acceptance.

The independent corpus uses exact ordinal raw `premium`, 1000 basis points,
and signed Int64 Money. Premium balances return
`truncateTowardZero(balanceMinor * 9 / 10)`; others remain unchanged. A BigInteger
oracle covers 10 classification, 10 rate and 54 Money cases, including whitespace,
case, negative rounding, values beyond Float precision and Int64 endpoints.
Accepted trials passed 158 independent behavior cases total. Flat S06/S07
stopped at documentation before their 64 planned cases; their behavior is
unverified by this corpus. The proposed task-bank S07 one-unit expectation was
corrected from one to zero to match the public truncation rule.

Both failed Flat trials passed attached tests and reported complete own
instruction/branch coverage before library commit. Their attempts to attach
documentation left `describe.documentation` empty. Flow supports `doc "..."`
inside a word; comments and extra protocol fields do not populate it. This is
metadata discoverability friction, not evidence of unsupported documentation or
incorrect arithmetic. The verifier requires documentation and examples alongside
tests, coverage, exact signatures, pure effects, persistence and source preservation.

Growing S06 and S07 each reused retained `customer.premium?`; S07 did not reuse
`customer.discount-basis-points`. Source review of Conventional's final
Operations.fs shows both later functions call retained `isPremium`; S07 likewise
does not call `discountBasisPoints`. Growing has runtime dependency evidence;
conventional reuse is a source witness, not an instrumented runtime graph.

The useful signal is that later fresh agents discovered and composed a typed,
tested classification abstraction. Growing S06 needed 14 calls and no protocol
diagnostics. S07 needed 47 calls and nine diagnostics. Conventional succeeded
with 17, 18 and 24 calls. This does not establish an efficiency advantage for
the language. The richer Growing fixture confounds retained vocabulary with
supplied examples/APIs. A reset-rich control and rotated repetitions are needed.

Coverage detects unexercised code but cannot establish correctness. Preflight
wrong trimming, rate, rounding and overflow controls pass deliberately limited
self-tests and own coverage, then fail independent behavior. Thirty control
outcomes passed. The deliberate overflow control triggers an eval error followed by a verifier error-formatting failure; its expected rejection is not a clean oracle mismatch diagnostic. This known diagnostic limitation remains for a subsequent verifier version. [Independent evidence review](evidence/076-independent-review.md) checked pins, traces and archived inventories. Setup failures remain separately saved: control syntax,
timestamp materialization, nominal Money inspection and inventory sorting were
corrected before actors ran. Unused first pins were archived before refresh.
Actual traces validate final runtime, prompt, oracle, verifier, host allowlist
and starting-project pins. All nine hosts closed cleanly. Archived final bytes
match acceptance inventories, including failed trials' pre-verification inventories.

Next shortest work: expose canonical documentation/example authoring and staged
replacement syntax in the compact primer/API help, then run fresh focused trials.
Do not repair or retrospectively reclassify these trials. Tests, examples and
documentation remain separate acceptance signals. The full 60-task suite, actual
LLM usage, constrained-context evaluation, broader adapters and provider parity
remain incomplete. F# coverage is not instrumented. Concurrent trials and
validation overlap prevent controlled latency conclusions. This study makes no
memory, arena, LLVM or native-performance claim.

Validation: fresh Release validation passed all 36 checks, with zero build warnings/errors, including 98 checks across the new isolated 30-control
business-policy preflight. See [saved local validation](evidence/076-local-validation.json).
Publication follows its passing gate; exact-source main CI is observed separately.
