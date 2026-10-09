# 150 — Shared-rule maintenance across billing consumers

Status: prepared controls and acceptance validation pass; fresh-agent results
pending. Starting revision: `ab1a62a`. No core/runtime changes.

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

## Interpretation boundary

This is one exploratory maintenance pair on a coordinator-shaped API, following
a task both languages solved correctly. It cannot establish a population-level
reliability advantage, autonomous abstraction quality, or model-token savings.
Preserve failures and actual source changes, and do not infer reuse from test
success. After the pair, consolidate what the recent trials establish rather
than automatically expanding the microbenchmark series.

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
