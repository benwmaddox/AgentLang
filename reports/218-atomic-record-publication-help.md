# 218 — Discoverable atomic record publication

The authoring help now explains the actual publication boundary for record
evolution: staged records and functions form a process-local group. Define the
complete change, run tests and publish in the same live CLI/JSONL session; a
fresh process reloads only published definitions. The replacement topic supplies
a machine-readable example:

```json
{"op":"commit","library":true}
```

It deliberately omits a function name so publication selects the complete group.
The expectedTypeSources guidance now covers every existing record declared in
the submitted source, including unchanged records; new record names have no entry.
This corrects the earlier misleading description of only records being changed.

Only AuthoringHelp.fs and its existing test method change. Existing examples and
schema fields remain; CAS, typing, atomicity, library qualification, coverage and
effects are unchanged. Four assertions check the public example's request shape.
No dependency, network listener, .NET escape or effect capability is added.

Fresh local validation passes 44 Flow Runtime groups and 2,195 assertions.
The ordinary sandbox build stopped before compilation with no useful error;
SDK info separately reported denied service-control access. A single-node build
without node reuse and with the workload resolver disabled succeeded. This is a
working validation configuration, not an isolated diagnosis of which setting
caused the earlier failure. The first test run hit storage access denial; setting
TEMP/TMP to a workspace scratch directory allowed the complete suite to pass.
No stale build output was used and CI was not run.

The sandbox build could not reach NuGet's vulnerability feed (NU1900 warnings).
Separate host-side read-only vulnerability checks for Core, CLI and Flow Runtime
tests, including transitive packages, exited zero and listed no known advisories.
That scoped result is not a general security audit or a guarantee of safety.
No packages were changed.

The change follows [216 preparation findings](216-typed-batch-failure-preflight.md)
and the [closed 217 continuation trial](217-typed-batch-failure-maintenance.md).
Both frozen trials were scored and sealed, and all 37 original runtime source
pins were checked before these post-trial edits. Their outcomes belong to the
old pinned runtime; this help change has not been tested in a fresh agent trial
and supplies no new efficacy result. Broker close controls belong in future
trial bootstrap instructions rather than language semantics.

Evidence: [validation receipt](evidence/218-atomic-record-publication-help/validation.json)
and [independent review](evidence/218-atomic-record-publication-help/review.md).
