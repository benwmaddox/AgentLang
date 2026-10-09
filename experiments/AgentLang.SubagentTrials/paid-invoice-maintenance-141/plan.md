# Paid-invoice maintenance comparison — preparation plan

## Objective

Run a bounded four-agent maintenance comparison: two fresh retained Flow/2
agents and two fresh conventional F# agents. Starting behavior and vocabulary
come from the accepted retained and F# replica-two outputs in report 135. Each
agent changes the existing shared customer payment aggregation so that a
payment counts only when its linked invoice exists, belongs to the queried
customer, and is `Paid`.

This is a small maintenance feasibility study. It describes results per agent
and does not estimate comparative reliability or a language effect. The
retained helper was created during an earlier agent study; the conventional
helper was coordinator-supplied in report 135.

## Scope and inputs

- Retained Flow start: report 135 accepted replica two.
- Conventional F# start: report 135 accepted replica two.
- Two fresh participants per language, each independently copied from the same
  frozen start. No prior participant work or conversation is included.
- The only coordinator additions to the accepted sources are paired,
  test-only imported-record fixture builders. They let both languages exercise
  open-invoice and missing-invoice payment rows which normal public payment
  submission cannot create. The builders are frozen, named as fixtures, and
  forbidden in production call paths.
- Public task states the shared operation's signature contract
  (`Store, CustomerId -> Result<Money, BusinessError>`) without naming the
  existing helper to discover.

## Frozen independent cases

`oracle.json` specifies four rows and expected results for the selected shared
operation, account summary, and dashboard. The rows cover signed Int64
addition, a payment attached to an open invoice, a payment with no invoice,
filtering before overflow, paid-payment overflow, unknown-customer precedence,
and preserved dashboard counts. Imported open/missing/negative rows are
explicitly defensive cases outside normal business invariants. For a paid
invoice, retain the old signed checked-add behavior.

The baseline is expected to fail at least the open-invoice and open-before-
overflow cases. A correct control adds the paid-invoice guard to the shared
payment fold. A separate incorrect control counts only positive payments even
when the invoice is paid; the signed paid row must reject it.

## Scoring and stopping

Report these dimensions separately for every participant:

1. **Behavior:** all oracle results for the actual shared operation, account
   summary and dashboard match exact nominal values/counts/error codes.
2. **Structure:** both summaries use the same discovered operation; one
   customer aggregation implementation performs lookup, status/owner filtering
   and checked accumulation. Dashboard-level summation remains checked and
   error-preserving.
3. **Collateral preservation:** inherited public signatures, complete customer
   value, all seven dashboard counts, unrelated inherited tests and pure Store
   semantics remain intact. The fixture helper is never used in production.
4. **Tests and qualification:** inherited and added regression tests pass after
   fresh reload. Flow target and required support functions pass existing
   library gates; no gate or inherited test is weakened. F# must build and pass
   its complete attached suite.
5. **Finalization:** Flow explicitly commits the task and closes cleanly;
   F# saves its changes and completes validation.

No replication beyond two per language is planned. A scorer/harness failure is
reported as a harness issue and cannot be counted as an agent failure.

## Preparation checklist

- [x] Inspect report 135 and roadmap recommendation.
- [x] Locate the accepted replica-two Flow and F# outputs and record source
      provenance.
- [ ] Add only the paired test-fixture scaffold to copies under this study.
- [ ] Freeze exact participant task, oracle, acceptance and start hashes.
- [ ] Run baseline, correct and plausible-incorrect controls for both languages.
- [ ] Freeze scorer/control hashes and launch instructions before dispatch.
- [ ] Dispatch only after root approves the frozen package.

## Reuse commands

Use the fresh CLI artifacts built for this study; do not rebuild them during
scoring:

```powershell
$flowCli = '.agentlang/maintenance-141/runtime-artifacts/bin/AgentLang.Cli/release/AgentLang.Cli.dll'
$fsharpCli = '.agentlang/maintenance-141/conventional-artifacts/bin/AgentLang.Conventional.Cli/release/AgentLang.Conventional.Cli.dll'
```

Use the existing R09 test/scoring interfaces where they match the frozen
fixtures. Run local .NET builds serially with
`-p:NuGetAudit=false -p:BuildInParallel=false -m:1`; give every F# control an
isolated `--artifacts-path` so controls never race or reuse stale binaries.
Exact invocation and scorer artifact paths will be recorded in
`preflight-controls.json` before any participant is dispatched.
