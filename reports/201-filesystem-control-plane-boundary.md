# 201 — Reserve host-managed dictionary paths from language I/O

Status: accepted after focused and full local Release validation.

A focused filesystem review found that a valid `fs.write` grant also authorized
writing `.agentlang/store/CURRENT`. This is AgentLang's authoritative dictionary
pointer, not ordinary application data. Root reproduced the issue with the
previous pinned Release runtime in a fresh disposable project: a library
sentinel committed normally, `file.write` replaced the pointer with `not-json`,
and the next process failed to load. No working or participant project was
altered. This is a resource-scope gap, not an effect-name bypass.

The bounded fix reserves the canonical root `.agentlang` subtree and root
`dictionary.agent` legacy dictionary/export from real language reads, writes
and existence checks, even with the corresponding effect granted. The shared
resolver returns `EFFECT_FILE_PATH_RESERVED`. Host semantic commands retain
their storage access. Test and explicit virtual providers remain isolated and
unchanged. Ordinary neighboring names remain accessible.

On Windows, ambiguous path segments are rejected before canonical resolution:
trailing dots/spaces, alternate-stream colons and DOS short-name alias spellings.
Exact `.` and `..` navigation still undergo normal canonical containment checks.
Reserved-name comparison is case-insensitive on every platform and respects
directory boundaries, including on case-insensitive filesystem mounts outside
Windows. This adds no package, external capability or native ABI.
The conservative Windows short-name rule also rejects otherwise ordinary
filenames containing `~` followed by digits, such as `draft~1.txt`.

The acceptance regression must verify an actual committed pointer is unchanged
after all denied operations, inherited library code reloads in a fresh process,
legacy dictionary bytes remain unchanged, and ordinary real files still work.
It also exercises normalized paths, reserved roots, each file operation and
Windows alternate spellings. The full local Release gate passed all 37 checks
on the frozen implementation; source hashes remained unchanged throughout.

The final focused CLI suite passes 12 groups and 249 assertions after a fresh
Release solution build with zero warnings/errors. Failed preparation builds
and an initial test invocation remain in the evidence; the invocation omitted
the fixture's explicit test-only write grant required for library reload.
Correcting that test grant does not grant production writes or relax the
reserved-path check. Source review found no concrete bypass of the frozen fix
within its stated boundary. The legacy-file test establishes an existing
fixture's byte preservation; it does not independently exercise a separately
absent legacy export.

Validation commands:

```powershell
dotnet build AgentLang.sln -c Release -m:1 -p:NuGetAudit=false
dotnet run --no-build -c Release --project tests/AgentLang.Cli.Tests
pwsh -NoProfile -File scripts/Validate.ps1 -Configuration Release -SerialBuild -SkipPackageAudit -ReportPath .agentlang/security-boundary-201/full-validation.json
```

Package auditing was explicitly skipped; this change introduces no dependency.
All checks ran locally. CI auto-runs remain disabled.

The [validation archive](evidence/201-filesystem-control-plane-boundary/validation.zip)
and [member index](evidence/201-filesystem-control-plane-boundary/validation.index.json)
retain the disposable baseline reproduction, preparation failures, final focused
logs, independent source review, full gate and its subordinate reports, lifecycle
receipt and accepted source snapshots. The archive contains 46 verified members,
1,657,249 bytes, SHA-256
`c2a5dc4482f11ab07d7836f9571e400ba95fd501ce3c6c119732da6b573dc91e`.

This is a path-based prototype policy. It does not provide an OS sandbox against
concurrent hostile filesystem changes or pre-existing hard links. Existing
reparse checks reduce accidental traversal but do not close those limits.
