# Efficacy plan 184: typed reference maintenance

## Task and comparison design

Milestone 183 was accepted and pushed as `49a7f30`. After control calibration, run one held-out maintenance task in a small, deterministic shipment-tracking seed. Add idempotence to scan ingestion: replaying a scan reference for the same shipment leaves the full Store unchanged; the same reference on another shipment is independent. Introduce a nominal String `TrackingReference`, distinct from `ShipmentId`, and thread it through the shared ingestion rule and its single-event and batch callers. The task names the domain distinction and behavior, but does not prescribe the type syntax or constructor spelling. This tests discovery of established vocabulary, one type migration, and reliable multi-function edits without repeating report 179's preview repair/record migration. It does not test vocabulary accumulation across separate tasks or agents, or establish general vocabulary growth.

All conditions must start with the same nominal `ShipmentId` type, record fields, semantic entry signatures, documentation, fixtures, and public behavior/error contract; F# uses the equivalent single-case type. Retained starts with a qualified reusable reference-lookup helper and its tests/docs. F# has an equivalent documented/tested helper available in source. Reset-rich has the same available types and semantic coverage but uses lower-level traversal instead of that reusable helper. The vocabulary is setup-created by AI, not organically retained from an earlier participant; do not invent prior history. The comparison is about access to established vocabulary plus migration to the new `TrackingReference`; type availability and public contract are not treatment differences.

Freeze 18 independent cases before dispatch: first scan; same-shipment replay; replay within a batch; same reference on a different shipment; identical raw text in a shipment ID and scan reference; batch ordering; existing errors; and preservation of unrelated Store fields. Duplicate equality is exact and scoped to one shipment; keep the first scan. Do not normalize or validate the wrapped String. The independent oracle checks complete state and error codes, plus a negative type control proving `ShipmentId` cannot fill a `TrackingReference` input. Calibrate four controls before launch: correct code/tests pass all cases; always-append fails same-shipment replay; global cross-shipment deduplication fails the same-reference-on-another-shipment case; and that global-dedup mutant with deliberately matching incorrect tests demonstrates that library qualification can pass while the independent oracle rejects it. All controls must execute without setup errors.

## Comparison and endpoints

Use one fresh stage per participant, two participants in each condition, in order: retained AgentLang, reset-rich AgentLang, F#, F#, reset-rich AgentLang, retained AgentLang. This six-actor design is a bounded pilot; it can expose failure modes but cannot establish a reliability ranking. Give each a single existing V2 broker session, frozen allowlist, empty capabilities, and 100 exchanges. No interim oracle feedback.

Score behavior, nominal type identity/use across the helper and both callers, inherited assertion/example preservation, AgentLang library qualification, task finalization, and recovery separately. Preserve inherited tests/examples; permit additions but no weakened or replaced assertions. Report exchanges/bytes/duration as protocol activity only. The type has identity semantics and no validator; claim no security or sanitization benefit, and make no token/context, throughput, or service-performance claim.

## Reuse and commands

Reuse `scripts/Start-SubagentTrialHostV2.ps1` and `scripts/Audit-SubagentTrialTerminationV2.ps1`. Use the structure of `experiments/AgentLang.SubagentTrials/preview-repair-178/score_preview.py` for a small task-specific `score_reference.py`; its preview fixtures are not reusable acceptance. Add only the task seed, frozen cases/model/scorer, prompts, and run evidence under `experiments/AgentLang.SubagentTrials/typed-reference-maintenance-184/` and `reports/evidence/184-typed-reference-maintenance/`. No new packages or broker features.

Build from the accepted revision into a fresh artifacts directory:

```powershell
dotnet build src/AgentLang.Cli/AgentLang.Cli.fsproj -c Release --artifacts-path .agentlang/efficacy-maintenance-184/build -m:1 -p:NuGetAudit=false
```

Launch each AgentLang run with its frozen exact allowlist and unique trace:

```powershell
pwsh -NoProfile -File scripts/Start-SubagentTrialHostV2.ps1 -CliDll <fresh-cli-dll> -ProjectPath .agentlang/efficacy-maintenance-184/runs/<run-id>/project -TracePath .agentlang/efficacy-maintenance-184/runs/<run-id>/trace.jsonl -AllowedOperations @(<frozen-operations>) -Profile agentlang -Capabilities @() -ClockValue '2000-01-01T00:00:00Z' -MaxRequestBytes 262144 -MaxResponseBytes 524288 -ExchangeTimeoutMilliseconds 120000 -MaxExchanges 100
```

Score only captured terminal candidates:

```powershell
python experiments/AgentLang.SubagentTrials/typed-reference-maintenance-184/oracle/score_reference.py --arm flow --project <captured-project> --cli <fresh-cli-dll> --label <run-id>
python experiments/AgentLang.SubagentTrials/typed-reference-maintenance-184/oracle/score_reference.py --arm fsharp --project <captured-project> --label <run-id>
```

## Blocker and evidence

Do not freeze or dispatch against the current dirty checkout. Milestone 183 must first complete its full acceptance; build and pin the study from a clean accepted revision, meaning the committed tracked source and documentation inputs used for the seed/runtime hashes. Preserve unrelated untracked files; leave `business-policy-retention-004` untouched and do not use it as an accepted baseline. The requested reports contain no ready shipment seed or task-specific oracle, so those must be prepared and all four controls calibrated before dispatch. Seed and scorer drafts are now being calibrated. No experimental participants have been dispatched.

This follows report 182's recommendation for held-out maintenance with behavior, preservation and recovery as separate endpoints. Reports 178–179 establish the calibrated independent-oracle and existing-broker workflow, while documenting why this must be a new task and must not be pooled with their preview sequence.

## Candidate evidence audit before reporting

Use terminal-only captures, followed by the existing V2 termination auditor.
Keep baseline and captured inventories, input manifest hashes, trace bytes and
all scorer receipts. Check every freeze pin again before each dispatch and
before scoring; a changed input requires a documented amended freeze before
further participants, without rewriting earlier inputs or results.

Preservation is a separate explicit baseline-to-candidate review. Enumerate all
inherited tests/examples and seeded helper names, compare each source assertion
and expected domain value, and record any change. Nominal constructor/type
annotation changes are permitted; removed cases, weakened assertions or changed
expected semantics fail preservation. In F#, review every inherited assertion
in tests/Program.fs and the documented public/helper declarations. In AgentLang,
query captured tests/examples/source metadata on an isolated copy and compare
with the seeded equivalents, including stable IDs and library maturity. Passing
candidate selftests alone cannot establish preservation. Save a per-candidate
review identifying each inherited case and any permitted typed adaptation.

For AgentLang, require an observed successful task.commit response in the
ordered broker trace, then verify the intended definitions persist when the
captured project reloads. Score task finalization separately from behavior and
library qualification. For F#, retain the fixed validate response/exit result;
this is local validation, not an equivalent runtime transaction. Score normal
broker host.close and terminal host/runtime exits separately in every condition.

For recovery, enumerate unsuccessful runtime/broker responses in trace order,
record the subsequent action and any demonstrated correction, and distinguish
resolved errors, unresolved errors and observation gaps. Link each entry to its
exchange index and source/test change. Do not equate a final passing test with
resolution of every prior error. Report exchanges/bytes/duration as protocol
activity only; participant reasoning turns and model token usage are unavailable.

The scorer supplies behavior/type/helper endpoints. Preservation, qualification,
finalization, caller wiring and recovery require these additional captured-source
and trace audits; placeholders in scorer output are not accepted findings.
