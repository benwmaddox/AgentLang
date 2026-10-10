# 186 — Type replacement diagnostics and implementation boundary

Status: accepted after complete local validation, 2026-10-10.

The [interrupted maintenance trial](185-typed-reference-maintenance-results.md)
found that existing record schemas cannot be replaced through the public
protocol. A type-only replacement could report a requirement for two functions,
concealing the more relevant limitation. This change makes that limitation
discoverable before an agent attempts a word replacement workaround.

The implementation rejects type-bearing Flow replacement requests with a
structured error identifying the declaration, and documents the immutable schema
boundary in replacement help. Record, scalar and enum replacement remain
unsupported. Word-only replacement, strong type checking, library qualification
and capability enforcement retain their existing rules.

Help metadata gains an additive `limitations` array. Its existing schema version
1, topic order, request formats and source syntax selectors remain unchanged;
the help/CLI fixtures and new limitation assertions cover those contracts. There
is no native layout or ABI change. The rejection runs immediately after parsing,
before word-count/CAS checks or any dictionary staging.

The next implementation is specified in [Atomic type evolution](../docs/TYPE-EVOLUTION.md).
It starts with record field changes and a single checked publication of the
schema and its affected functions, tests and examples. It requires stale-edit
protection, preserved inherited assertions, library coverage, rollback and fresh
reload. It must not reinterpret existing values or native artifacts using a new
same-name schema.

The focused Release build passed with zero warnings/errors, and Flow runtime
checks passed 42 groups / 1,891 assertions. The rejection matrix includes
record/scalar/enum-only declarations, type-plus-one-function and type-plus-many
requests, an already persisted record's Int-to-String field change, unchanged
add-only duplicate handling, generated signatures, abort and fresh reload.
Existing word-only replacement cases run in the same suite.

The complete local validation gate passed all 37 checks with terminal exit 0.
Its business-policy preflight passed 98 checks across 30 independent outcomes.
The diagnostic change is runtime engineering,
not another agent efficacy result or a cybersecurity certification.

## Preparation attempts

The first project build failed without a surfaced diagnostic. Its detailed log
is retained. A serial compiler run then caught a missing string annotation in
the new test helper, which was corrected. The first focused execution used the
default OS temporary directory and failed with `STORAGE_ACCESS_DENIED`; tests
passed with process `TEMP` and `TMP` set inside the workspace. The implementation
worker's receipt summarizes these attempts; exact compiler/default-temp failure
transcripts are not separately archived. The passing focused output is retained.
The verbose MSBuild environment/property dump remains local; the archive includes
its terminal failure excerpt and original checksum.

The full local command also uses workspace temporary storage:

```powershell
pwsh -NoProfile -File scripts/Validate.ps1 -Configuration Release -SerialBuild -SkipPackageAudit -ReportPath .agentlang/type-diagnostic-186/full-validation.json
```

No capability, external service, dependency or persistent format is added.
Existing scalar validator freezing and library coverage requirements remain.

## Capability boundary review

A read-only source review found no production leakage from test-file dependency
replacements. Tests use virtual filesystem providers; replacement dispatch is
attached to test execution and still checks the original operation's effects.
Existing focused checks also verify production evaluation reads the real file
and denied effects cannot be granted by a fake.

Real filesystem access checks relative paths and existing reparse points, but
checking a path and opening it are separate operations. A concurrent writer able
to replace project directories can race that check. The provider already
explicitly documents this limit. Protect the project tree from untrusted
concurrent writers; the runtime is not an OS security sandbox. Race-resistant
handle-based filesystem access or host OS isolation remains a hardening item.
No external probing was performed.


Evidence: [archive index](evidence/186-type-replacement-diagnostics/index.json) and [tested sources and receipts](evidence/186-type-replacement-diagnostics/evidence.zip). The archive includes the read-only capability boundary review; the verbose failed-build environment dump remains local.
