# 233 — Explicit inherited-test change intent

Status: implemented; local validation and mechanical controls passed.

The runtime is being changed to reject undeclared inherited-test edits during Flow definition replacement. This addresses the outcome-inert fixture drift observed in [report 231](231-grouped-renewal-timing-comparison.md). It is an edit-intent and stale-source guard; it cannot determine whether an agent's declared edit fits the task.

## Contract

A definition replacement that changes an existing inline test's source or syntax version must include a `testChanges` row:

```json
{"word":"subscription.renew-monthly","caseName":"existing-case","expectedSourceHash":"<current test source hash>"}
```

Obtain the hash through `tests` with `includeSource: true`. The same check applies to single-function, atomic multi-function and record-evolution replacements. Identical source/version and new cases need no row; omitted tests remain attached. Missing, stale, duplicate, unused and malformed rows fail before staging. Dedicated attachment/test-file editors retain their explicit edit purpose and existing revision checks. Stack-to-Flow migration remains a separate legacy boundary.

No persisted schema, typed IR or ABI change is intended. No host capabilities, dependency packages, network listeners or arbitrary .NET invocation are added.

## Validation and controls

The fresh focused Release build passed with zero warnings and errors; runtime tests passed 45 groups and 2,426 assertions. Tests cover successful explicit edits, unchanged/new/omitted cases, rejected outcome-inert fixture drift, stale/malformed/duplicate/unused rows, unchanged candidate and durable state on failure, version-only changes and dedicated test editing. Source-inclusive attachment snapshots also remain unchanged after single-function, multi-function and record-evolution rejection. The focused build reused restored dependency assets; full repository validation completed with 36 of 37 checks passing in the sandbox. The sole failure was `business-policy-preflight`: NuGet vulnerability-feed access failed with `NU1900`. The exact failed preflight was rerun on the host with package auditing enabled and passed all 98 checks. Both the sandbox failure and successful host retry are retained; this is combined validation evidence, not a claim that the sandbox run was entirely green.

Independent coordinator controls compare the prior accepted runtime and the new implementation. The previous runtime accepted the one-function and two-function outcome-inert fixture changes. All four updated controls passed: each undeclared change rejected with `FLOW_TEST_CHANGE_REQUIRED` without staging or changing durable state; supplying the exact inspected old hashes allowed publication. Both accepted paths passed their library tests. These are mechanical runtime checks, not fresh-agent efficacy evidence. Runtime and working-source pins remained unchanged during the controls. Combined local validation passed as described above; the evidence archive retains the source snapshots, controls and both validation attempts.

## Research interpretation

The guard can reject an accidentally resubmitted case before publication. An agent can still deliberately declare an unrelated edit or use an explicit test-edit operation. Independent behavioral acceptance and source comparison remain necessary. Passing library coverage cannot establish fixture preservation when changed data leaves the observed result unchanged.

The active delivery priority is now Campfire implementation. Observe the guard during subsequent application maintenance rather than scheduling another standalone comparison before application progress. Keep permitted inherited case changes independent of participant requests; measure accepted behavior, collateral edits, guard rejections/recovery and interaction cost. Do not score `testChanges` rows as task authorization or infer a reliability rate from these mechanical controls.

## Evidence

See [evidence archive](evidence/233-inherited-test-change-intent/evidence.zip), [member index](evidence/233-inherited-test-change-intent/index.json), and [archive receipt](evidence/233-inherited-test-change-intent/receipt.json). The archived aggregate receipt records 36 sandbox passes and the successful audited host retry of the sole blocked check, alongside the original failed run.
