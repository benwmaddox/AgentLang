# Atomic subscription handoff comparison

Status: preparation only. No participant outcomes or efficacy claim yet.

Research question: can a fresh coding agent discover and compose existing typed
transitions while preserving validation order and collateral state? This is a
different operation from the closed billing and file-publication sequences.
Mandatory library coverage remains enabled. Independent behavior checks supplement
it; they do not replace or weaken it.

## Public task

Add one pure subscription handoff operation. Given a Store, old subscription ID,
replacement ID, replacement term, handoff instant and replacement expiry:

- End the old subscription at the handoff instant and create an active replacement
  for the same customer and product, beginning at that exact instant.
- Preserve the existing cancellation rules and their error precedence. Only after
  those succeed, apply existing creation rules and their error precedence.
- On success, return the final Store. Preserve every unrelated entity and all old
  subscription fields except status and cancellation timestamp. Trim the new term.
- On failure, return the existing error code; do not report the intermediate
  cancelled Store as a successful handoff. The caller's input remains unchanged.
- Preserve existing functions, types and tests. Add meaningful regression tests.

AgentLang target: `subscription.handoff(Store, SubscriptionId, SubscriptionId,
String, Instant, Instant) -> Result<Store, BusinessError>`, published as library.
F# target: an equivalent editable operation returning `Result<Store, DomainError>`.
Parameter names may follow each language's established convention.

The dispatched task describes behavior without naming implementation helpers.
Reuse is encouraged in the common primer, but not mandatory acceptance. Record
whether the final call graph uses retained cancellation and creation operations.

## Matched starts and constraints

Use current business subscription/occupied-period APIs in both conditions. There
is no handoff implementation in either seed. AgentLang's operations return a
Store; F#'s underlying transitions return `(Store, Subscription)` on success.
This difference is disclosed, not hidden behind a claim of identical interfaces.

Run one fresh Luna/max participant per condition sequentially, without inherited
conversation, at most 100 broker exchanges. Reuse `Start-SubagentTrialHostV2.ps1`
and its termination audit. No new agent platform or broad retention runner.
Prompts authorize only their assigned project and local validation; forbid sibling
inspection, collaboration, hidden-oracle inspection and non-broker edits.
These remain prompt-level restrictions unless separately enforced.

## Acceptance before dispatch

Build pinned current Release runtimes and baseline suites locally with serial
builds and `NuGetAudit=false`. Prepare exact project copies and inventory them.
Score both controls and participants on disposable copies. Hidden expectations
must not enter participant projects or depend on participant-authored tests.

The independent model uses discrete occupied time cells for a bounded fixture,
with half-open periods and explicit cancellation. Compare canonical entity
projections, not different map/list encodings. Also check language list order and
complete input preservation separately. Cases include boundary handoffs, empty
old occupancy, after-expiry handoffs, third-party conflicts, different identity
scopes, invalid replacement terms/periods/IDs, missing/cancelled old subscriptions,
and combined failures that distinguish validation order.

Before freezing or dispatching, a correct composed control must pass the actual
language and F# scorers. Faulty controls must execute and fail behavior, including
starting before cancellation, leaking the cancelled intermediate state, ignoring
overlap and checking duplicate replacement ID before cancellation errors.
Model-only mutation discrimination is preparation, not runtime verification.

Freeze only after every preparation worker has completed. Pin prompts, seed
inventories, runtime artifacts, cases, scorer source and control outputs. Never
remove or compact frozen outputs. Record any setup failure and any later pin
mismatch; do not silently rewrite failed evidence.

## Reporting

Separate independent behavior, preservation, final own-test adequacy, library
qualification, voluntary reuse, discovery and workflow completion. Existing tests
may satisfy regressions; do not demand redundant new tests merely by name.
Record broker errors, exchanges, duration and request/response bytes. Bytes are
not model tokens; exchanges are not model turns. A single matched pair cannot
estimate reliability rates or establish a causal retention advantage. Native
performance and arena policy are outside this trial.
