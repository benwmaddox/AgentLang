# Exact-Money policy vocabulary study

Status: all nine trials completed; seven passed full acceptance. Growing and
Conventional passed all three tasks. Flat S06/S07 failed documentation metadata
before independent behavior cases. See [results](results.json) and
[report 076](../../../reports/076-exact-money-agent-policy-study.md).

This focused study executes the task-bank dependency closure S01 → S06 → S07:
classify exact raw `premium`, return discount basis points, then return an exact
discounted Money balance. The returned premium balance is truncated toward zero:
`BigInteger(balanceMinor) * 9 / 10`; nonpremium balances stay unchanged. Every
signed Int64 input is valid, including negative values and both endpoints.
Money is never Float. The independent corpus includes raw whitespace/case
variants, rounding boundaries, values beyond exact binary Float integers and
Int64 limits. The original proposed S07 one-unit expectation is corrected to
zero to match its public rule.

Each task uses a fresh external Luna/max subagent with no inherited turns.
Growing starts with the six verified milestone-075 documents (53 supplied words,
31 types) and retains only independently accepted task definitions. Flat starts
each task from a Customer schema-only dictionary with equivalent nominal scalar
checks; it retains no earlier task words. Conventional uses the existing F#
nominal Business scalar types in a public Customer facade and retains accepted
policy functions. It preserves raw Kind instead of calling the reference's
trimming private Customer constructor. Its Instant wrapper preserves the
language's canonical UTC field boundary.

The three new policy words are task vocabulary; the 53 Growing foundation words
are supplied vocabulary. Dependency records distinguish these. Reuse is
observed, not required by a hidden structural acceptance gate. A failed task
does not contaminate later stages: Growing/Conventional dependent tasks require
a passing predecessor. Flat tasks are independent resets and still run after
an earlier failure. No mode receives coordinator-written task solutions.

Only disposable synthetic projects are actor-editable. Host oracles, known-correct
controls, sibling projects and prior solutions stay outside the broker roots.
The conventional broker fixes its local validation command; this is an
instruction/protocol boundary, not an operating-system sandbox. There are no
real payment/email providers or effects in these tasks.

Before launch, freeze the runtime, source, input dictionary/project, public
prompt, host allowlist and oracle hashes. Run the verifier on correct controls,
no-op seeds and wrong behavior that passes self-tests. Preserve requests,
responses, trace audits, final state and acceptance separately for every actor.
Retain the same live session across observation timeouts; actors tear down
their own hosts with Ctrl+C after acceptance, without extra JSONL requests.

This is one exploratory three-task sequence, not completion of the 60-task
bank or controlled context/token evaluation. Record actual protocol bytes,
calls, diagnostics and elapsed time. LLM tokens, model turns and effective
context windows remain unknown unless a real usage source supplies them.
Differences between the schema-only Flat and richer Growing fixtures remain
visible; a reset-rich control may later isolate task retention alone.
