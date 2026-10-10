# 176 — Preserve retained test and example source formats

Status: completed local engineering milestone, 2026-10-10. This is not a new
agent comparison.

## Problem and change

[Trial 175](175-email-fifo-batch.md) exposed avoidable preservation friction.
Replacing a Flow/1 function with Flow/2 source rejected its unchanged tests and
example because their syntax versions differed. The agent later rewrote those
attachments, including changing an example's observable output. Passing behavior
tests did not establish exact collateral preservation.

Each retained attachment now keeps its own explicit source format. A replacement
can advance the function source while retaining the exact test/example source
bytes, hashes and binding identities. Attachment-only edits use the submitted
attachment version against the owner's stored definition version. Test-file
wrappers remain Flow/2; removing a case reparses a retained wrapper at its own
version. Stack/Flow mixtures remain invalid.

Manifest format 5 stores a deterministic, complete map from distinct attachment
source references to formats. Loading rejects missing, extra, duplicate,
unsupported or cross-frontend entries. The table is bounded by the existing
attachment reference count before row decoding. Older manifests retain their
documented owner-format interpretation; old-format serialization rejects loss of
independent attachment formats. Call-binding validation selects the format by
body role and exact source reference, retaining source, ownership and identity
checks. No source-text guessing or automatic source translation is used.

Flow/2 also accepts a leading dot on the six existing generic container
constructors and namespace-qualified static callback references. These forms
lower to the existing AST and canonical source forms. Ordinary root calls still
require parentheses; unknown generic calls and Flow/1 aliases remain rejected.
There are no new generic language semantics, runtime effects or native ABI changes.

## Validation

The accepted focused run (008) builds the solution in Release with zero warnings
and errors, then passes:

- Flow parser: 1,302 assertions.
- Discovery: 140 assertions.
- Storage: 17 groups / 491 assertions.
- Flow runtime: 42 groups / 1,715 assertions.

Final engineering replay 003 uses the original blocked exchange 24 from trial
175 against a copied seed. The owner's ID is preserved, its revision advances
to 2, and the exact three test references, example reference and attachment
bindings survive commit. The example still returns the String
`"NO_PENDING_EMAIL"`. All 191 saved tests pass both before and after a fresh CLI
reload. The replacement is accepted as library vocabulary. This replay is not a
fresh agent efficacy trial.

The mixed-project regression also retains generated record accessor cases,
standalone Flow/1 cases, Flow/2 cases and unrelated test-file wrappers across
manifest-v5 reload. Two unrelated wrappers include a scoped dictionary override;
source bytes, references, bindings, case/settings metadata and their owner's
revision stay unchanged through staging, commit and reload. The override still
executes in its test scope.

The full local gate passes all 37 checks on the same frozen source, including
98 business-policy preflight checks across 30 independent outcomes. The 34
source pins match the accepted focused run and replay. The command is:

```powershell
pwsh -NoProfile -File scripts/Validate.ps1 -ReportPath <isolated-evidence>/validation.json -Configuration Release -SerialBuild -SkipPackageAudit
```

Package vulnerability audit is skipped by that local correctness command. This
report does not represent a security audit or a vulnerability-free guarantee.

### Failures retained in evidence

Focused attempts 001–007 are retained with their logs and matching 34-file source
snapshots. The first two failed compilation in new tests; later attempts exposed
fixture ordering/request errors and legacy decoded-map expectations. The first
replay's in-process checks passed but fresh reload failed. Validation and
follow-up review caught three production omissions, all fixed with regressions:

- The nested type-source parser did not accept manifest v5.
- Aggregate recovery skipped generated record/scalar cases under v5.
- Single-owner edits replaced the whole wrapper map, losing unrelated owners'
  wrappers while their test rows survived.

Initial source review missed the type-reader omission. Follow-up review found the
generated-case gate; focused validation caught the unrelated-wrapper loss. Final
source review reports no outstanding findings. Passing review alone was not
accepted as validation.

The [evidence index](evidence/176-retained-attachment-source-versions/archive.json)
and [archive](evidence/176-retained-attachment-source-versions/evidence.zip) retain
failed attempts, accepted results, source pins, plans, review and replay projects.
Archive CRC and every entry hash are verified by readback. Each archived replay
CURRENT pointer is parsed as JSON and its referenced final manifest is verified
and included. Earlier trial-175 evidence remains unchanged.

## Scope and limits

This addresses a demonstrated editing obstacle. It does not reverse trial 175's
preservation failure or establish a reliability advantage over F#. No external
email/network calls, new effect providers, arbitrary .NET invocation or dependency
changes are introduced. Local correctness validation is not a security audit.
