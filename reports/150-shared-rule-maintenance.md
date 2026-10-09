# 150 — Shared-rule maintenance across billing consumers

Status: both fresh agents pass all 48 hidden cases, preserve shared calculation,
and complete normally. No comparative reliability advantage is established.
Starting revision: `ab1a62a`; preflight frozen in `05b6ed0`. No core/runtime changes.

Report 149 showed two fresh agents discovering and reusing a coordinator-shaped
validated-window API. This follow-up tests reliable maintenance: change the
shared rule so that each rental's original start day is free, consistently in
the validated calculation, singleton query, and list-total query. Splitting
billing into several windows must not grant additional free days.

## Frozen task design

The participant starts are exact, inventoried copies of report 149's final
projects: AgentLang 95 source/storage files and F# 8 source/project files, excluding
build products and the lock file. Baselines are 47 AgentLang tests and 64 F#
checks. Controls are separate copies and are never participant inputs.

Run one fresh Luna/max participant per language sequentially, with 100 broker
exchanges, the unchanged 148 AgentLang runtime/help, and 146 conventional discovery.
The public task specifies behavior across all existing billing APIs, allows
updating expectations that the policy legitimately changes, and prohibits
removing tests merely to pass. AgentLang must retain existing library gates and
maturity. No helper or implementation strategy is mandated. Reuse and propagation
will be inspected separately from behavioral acceptance.

Explicit isolation rules prohibit sibling inspection, messages, or delegation.
These are prompt-level constraints, not a per-agent enforced tool whitelist.
Model settings describe coordinator dispatch requests, not provider telemetry.

## Preflight evidence

Independent expected values enumerate bounded stored rental days, require
`day > original start`, and intersect with the requested window and cancellation.
They are not computed by calling or copying either control. The preserved
report 145/147 scoring harnesses were adapted for this rule and direct-helper
calls. There are 21 singleton, 9 direct-helper, and 18 batch cases: 48 per language.
Cases include split windows, one-day rentals, cancellation at start/nextday,
invalid empty batches, duplicate/overlapping rentals, preserved input fields and
list order, and signed 64-bit request endpoints.

| Check | AgentLang | F# |
| --- | ---: | ---: |
| Correct control own suite | 61/61 | 74/74 |
| Singleton acceptance | 21/21 | 21/21 |
| Direct-helper acceptance | 9/9 | 9/9 |
| Batch acceptance | 18/18 | 18/18 |

The AgentLang control changes the existing shared helper and updates affected
tests/examples. Fresh reload preserves library qualification: helper 31/31
instructions and 4/4 branches; singleton 12/12 and 2/2; batch callback 10/10 with no
branches; aggregate 19/19 and 4/4. Its two helper examples pass. F# builds fresh in
serial Release with zero warnings/errors and its executable suite passes.

Both unchanged originals compile/define and execute all cases, failing 9 singleton,
4 helper, and 9 batch cases under the new policy. Two additional F# mutations
verify specific failures while still executing every case:

- Waiving each window's first occupied day fails 4 singleton, 4 helper, and 1 batch
  case.
- Leaving only the batch consumer on the old calculation fails 9 batch cases;
  all 21 singleton and 9 helper cases still pass.

All control scores report input source unchanged. These checks validate that
the acceptance suite can distinguish the intended policy from old and partially
updated behavior. They are not fresh-agent efficacy results.

## Fresh-agent outcomes

| Outcome | AgentLang | F# |
| --- | ---: | ---: |
| Singleton acceptance | 21/21 | 21/21 |
| Direct-helper acceptance | 9/9 | 9/9 |
| Batch acceptance | 18/18 | 18/18 |
| Final own suite | 59/59 | 75/75 |
| Added tests/checks | 12 | 11 |
| Broker exchanges | 53 | 22 |
| Error responses | 1 | 1 |
| Shared production calculation retained | Yes | Yes |
| Host/runtime exits | 0/0 | 0/0 |

Both final implementations change the shared validated-window calculation and
retain the singleton and batch delegation paths. AgentLang increments the clipped
start only when it still coincides with the original start; F# clips against
`StartDay + 1`. Stored starts are bounded below 366, so this addition cannot
overflow even when request endpoints span the full signed range. Independent
day enumeration accepts both implementations across all three APIs.

AgentLang preserves all 47 inherited test names and adds 12. It updates legitimate
policy-dependent expectations, preserves signatures/types, and does not introduce
another production function. Fresh own-suite inspection reports library maturity
and current complete applicable coverage for the helper (39/39 instructions,
6/6 branches), singleton (12/12, 2/2), fold callback (10/10, no branches), and total
(19/19, 4/4). Both Result-returning queries observe ok and error tags.

The AgentLang trace records ten earlier failed assertions across four test runs
while expectations were being updated (2, 5, 2, and 1), followed by passing reruns
and 59/59 at the end. These are not ten unresolved final failures. Its one error
response is a malformed eval match expression. Tests can fail inside successful
protocol envelopes, so error-response counts alone do not describe test recovery.
The participant commits the task and closes normally without coordinator repair.

F# modifies only `project/Rental.fs`, `tests/DomainBaselineTests.fs`, and
`tests/BillingWindowTests.fs`. It updates affected assertions, preserves unrelated
checks, and adds regressions for original-start, split-window, one-day and
cancellation behavior. Fresh scoring runs its suite successfully with 75 checks.
Its one rejected patch has an invalid-length expected file hash, corrected before
applying the edit. The participant reports using in-memory hash-length checks
to handle terminal wrapping, in addition to prompt verification and broker work;
it reports no outside project information or agent contact.

Both scorers preserve the submitted source. Both participants report prompt hash
verification and no sibling information; no participant communication is observed.
AgentLang reports only prompt/broker use. Isolation remains prompt-level, and
requested Luna/max dispatch settings are not independently recorded by the broker.
The participants run sequentially. The differing own-test counts and broker
protocols cannot establish relative thoroughness, model-token cost, or latency.

## Interpretation boundary

This is one exploratory maintenance pair on a coordinator-shaped API, following
a task both languages solved correctly. It cannot establish a population-level
reliability advantage, autonomous abstraction quality, or model-token savings.
Source inspection confirms shared propagation separately from test success. Both
agents also succeeded on the preceding composition task. This pair adds a later
correct change, but still does not isolate vocabulary retention from language,
tool, agent variation, or coordinator API design. The next step is the consolidated
[efficacy checkpoint](151-efficacy-checkpoint.md), not another billing microtrial.

Raw source, commands, requests/responses, failed attempts, independent expectations
and provenance are preserved under `.agentlang/shared-rule-150/`. Preflight
archives and review will accompany the frozen participant dispatch.

## Frozen evidence

[Preflight archive](evidence/150-shared-rule-maintenance/preflight.zip) and
[entry index](evidence/150-shared-rule-maintenance/preflight-index.json): 1,423
entries, 5,659,122 bytes; SHA-256
`4625dd00f03b1dc7597e9a189a205a526948ab05db1f00782c934d6c4bd2ee50`.
Every archive entry was read back and hash-verified. It includes the selected
starts, positive and negative controls, oracle source and cases, frozen runtimes,
prompts, validation logs, source provenance and independent review. The reviewer
recomputed source/prompt hashes and confirmed all stated acceptance counts.
The two specific mutation probes are F# only; original implementations were
behaviorally rejected in both languages. No fresh participant had started at
this preflight checkpoint.

[Results archive](evidence/150-shared-rule-maintenance/results.zip) and
[results index](evidence/150-shared-rule-maintenance/results-index.json): 772
entries, 1,116,899 bytes; SHA-256
`d35af76ba4190e2466a23f56aea38bfd5500439acbec06683f8d79da209f66aa`.
The archive preserves traces, final sources, API scores, own-suite results,
post-test coverage, source differences, disclosures and independent review.
All 1,319 frozen nonparticipant inputs remain unchanged; final source hashes
match grading (122 AgentLang storage/source files and 8 F# files). Every results
entry was read back and hash-verified. The final reviewer found no blocking
discrepancy in the reported outcomes or their interpretation.
