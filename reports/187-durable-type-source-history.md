# 187 — Durable type source history

Status: complete local validation passed, 2026-10-10.

This is a prerequisite for the record-schema migration required by the
interrupted [maintenance trial](185-typed-reference-maintenance-results.md).
The trial remains closed; this engineering work is not a new efficacy result.

## Contract

Manifest format 6 adds required prior type-source revisions while retaining
current type heads. Rows identify the type, revision ordinal and exact source
reference, frontend format and validator metadata. Historical references join
storage's hash-verified source closure and existing resource limits before
reference deduplication. Older formats retain their existing serialization and
loading rules, including version-5 attachment metadata and version-1 pointer
and snapshot envelopes.

Read-only type inspection exposes the current source hash; type history returns
manifest-referenced sources without compiling historical schemas. Existing word
inspection/history and raw type source responses remain compatible. Transient
sources must not masquerade as durable revisions, and ambiguous selectors must
produce a structured error. No provider invocation, host capability or external
dependency is added.

## Acceptance

Checks cover deterministic encoding and history order, supported
older-format loading, required format-6 metadata, duplicate/noncontiguous/invalid
revision rejection, version-only downgrade rejection, reference limits, missing or tampered historical objects,
failed-write authority, snapshot restore, and fresh runtime reload. Preserve
existing word history payloads and the exact version-1 golden fixture.

Focused storage and Flow runtime console suites used fresh serial Release
builds with workspace temporary storage. The complete local gate passed all
37 checks with terminal exit 0. Its business-policy preflight passed 98 checks
across 30 independent outcomes. Tested source hashes and the version-1 golden
fixture hashes remained unchanged through validation and evidence packaging.

The full local command, with process `TEMP` and `TMP` inside the workspace, was:

```powershell
pwsh -NoProfile -File scripts/Validate.ps1 -Configuration Release -SerialBuild -SkipPackageAudit -ReportPath .agentlang/type-history-187/full-validation.json
```

Same-name record replacement is not part of this stage. It follows with type
source CAS, record backups, one type/function publication group, preserved tests
and library coverage, and rollback. The [type-evolution plan](../docs/TYPE-EVOLUTION.md)
retains the full migration and persisted-seed readiness requirements.

## Focused validation

Fresh serial Release builds passed with zero warnings/errors. Storage console
checks passed 18 groups / 572 assertions; Flow runtime console checks passed
42 groups / 1,934 assertions after the review fixes. The worker's concise receipt
records these commands. Independent review found two missing Stack frontend cases:
format-6 reload omitted generated tests/examples, and same-engine inspection of
newly committed validated types differed from inspection after reload. Both
now have corrections and regression coverage. Independent re-review found no
remaining concrete issue in that bounded pass. The complete local gate passed
against the frozen corrected sources.

The first complete local gate was deliberately interrupted for those defects
(outer session exit 1; wrapper receipt records interrupted child exit -1).
Its source pins, output and exit receipt were preserved with
an `interrupted-1-` prefix. It is not a passing validation result. After the
corrections, fresh focused builds and the complete local gate passed.

Preparation caught a record-label inference error, F# test argument/index syntax
errors, an additional version-5 serializer ceiling, and one test expectation
that omitted the language's quoted string rendering. These were corrected and
the focused suites rerun. Earlier individual failure transcripts are not
separately archived; these attempt details are implementation-session summaries.

The first new upgrade regression supplied an export captured before subsequent
commits. Storage rejected that stale fixture with `STORAGE_EXPORT_MISMATCH`.
The test was corrected to use the exact version-5 load's project source;
production export validation remained unchanged.

Review also identified repeated-source memory amplification. Closure verification
now deduplicates only after counting raw references. Type history reuses source
text and caps cumulative returned source UTF-8 bytes at 8 MiB, counting repeated
references in the output. This is a source-byte budget, not the exact encoded
JSON envelope size. These engineering checks do not certify an OS security
sandbox or establish agent efficacy.

Evidence: [archive index](evidence/187-durable-type-source-history/index.json)
and [tested sources and receipts](evidence/187-durable-type-source-history/evidence.zip).
The archive retains both the accepted full run and the deliberate interruption,
plus focused receipts, independent review and the storage compatibility pins.
