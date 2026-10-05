# External subagent vocabulary pilot

Date: 2026-10-04 (local; execution continued into 2026-10-05 UTC)
Status: both tasks independently verified and finalized. Exploratory demonstration, not a controlled performance comparison.

## Task 1: build tested discount vocabulary

A fresh `gpt-6-luna` subagent at max reasoning started with only the `Customer` record and trusted/generated words. It received a compact syntax primer and six runtime operations, discovered capabilities through search/describe, and defined `customer.premium?` and `customer.discounted-balance` with documentation and seven attached cases. Both words were accepted at library quality.

The balance word calls the new premium predicate; it covers all ten of its own instructions and both conditional outcomes. The predicate's three tests check premium, standard, and case-sensitive matching. The balance's four tests check premium, standard, case-sensitive matching, and a larger balance. The task log records fourteen test executions because commit validation reruns the seven cases.

Host acceptance ran in a fresh runtime process before final task commit. All **16 checks passed**: classification for premium/standard/capitalized/blank kinds; discounted balances including zero, negative, and larger values; rejection of an Int where Float is required; all seven attached cases; persisted library signatures and complete own-body coverage; and the original Customer constructor signature. The subagent then finalized the task successfully.

| Observed metric | Result |
| --- | --- |
| Runtime requests including task begin/final commit | 17 |
| Search / describe / eval / define / test / word commit | 4 / 5 / 1 / 1 / 2 / 2 |
| New library words / distinct attached tests | 2 / 7 |
| Runtime errors / test failures during the successful trial | 0 / 0 |
| Independent host checks | 16 passing |
| Agent-reported source fallback | None; the reviewed protocol trace also contains no source operation |
| Exact LLM tokens, model turns, framework context, full task elapsed time | Unavailable |

The supplied task prompt and canonical JSON interaction bytes are artifacts, not exact model context measurements. No byte-to-token estimate is presented as observed usage.

## Infrastructure findings

An earlier attempt failed before language work: the workstation sandbox shell could not start, and a runtime process ID created by the parent was inaccessible from the child. Its one attempted search never reached the language. The successful retry used authorized host-side shell execution and its own persistent JSONL process. Initial failure logs remain in `.agentlang/subagent-pilot-6d9e341/`.

The subagent framework later refused a fresh task-2 spawn due its thread limit. Task 2 therefore reuses a different earlier storage-implementation agent. It receives no task-1 conversation or source, but retains prior repository context. That limitation prevents treating it as a pristine fresh-context comparison.

## Task 2: discover and compose retained vocabulary

The host retained the accepted words and introduced a Subscription record with term and renewable fields. The task asks a different agent to discover the existing customer words and compose `customer.renewal-balance`: apply a further 5% reduction to the existing discounted balance only for exactly premium customers with annual, renewable subscriptions. Attached tests and library coverage are required; host acceptance remains separate.

The different agent discovered the retained words through search/describe and created one library word with eight tests covering every premium/non-premium × annual/monthly × renewable/non-renewable combination. Its three nested conditionals covered all **22 own-body instructions and six branch outcomes**. The implementation calls both retained customer words and does not duplicate their classification or discount logic. Existing words stayed at revision 1.

Independent host acceptance passed **17 checks**, including all eight eligibility combinations, case-sensitive kind/term variants, zero/negative balances, rejection of Customer where Subscription is required, all fifteen distinct project tests, library coverage, and direct retained-word dependencies. The agent then finalized task-0002. Its task log records sixteen executions of the eight new tests, zero failures, and zero language errors.

The second trial used **21 runtime requests**: 3 search, 13 describe, 1 define, 1 test, 1 word commit, and task begin/final commit. A terminal-wrapped response caused one tool-side JSON-capture error; repeated descriptions recovered it. This was not a language diagnostic. Recorded runtime-work time before host acceptance was **10 minutes 1 second**; the interval through host-approved finalization was **12 minutes 50 seconds**, including host wait time. Exact model turns/tokens/context remain unavailable. [Task-2 artifacts](../experiments/AgentLang.SubagentTrials/task-02/) preserve these observations.

This demonstrates that a different external agent can discover and compose accepted vocabulary through runtime metadata. No Flat/Conventional comparison, token saving, context-window result, or general correctness improvement is claimed. The prior context of the second agent and the different task complexity prevent interpreting 17 versus 21 requests as a cost trend.

## Reproduction and limits

The trial used the pinned previously validated Release runtime at **6d9e341c402144f5d168566e119906e5495e3cb4**, with binary hashes saved in the host manifest. [Task-1 artifacts](../experiments/AgentLang.SubagentTrials/task-01/) preserve the initial/final source, prompt, interaction trace, notes, and independent acceptance results. The interpreter still executes checked ASTs; this trial does not establish semantic-IR implementation.

Agents had broader host tools; protocol-only use was enforced by instructions, not an OS/tool sandbox. Float balances are a toy model and do not establish exact financial arithmetic. Own-body coverage demonstrates supported code-path execution, not correctness over every possible input or transitive library rigor. Exact model usage is unavailable from this subagent interface.
