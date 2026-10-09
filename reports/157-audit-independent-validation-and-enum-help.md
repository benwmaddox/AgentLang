# Audit-independent validation and enum authoring help

Status: completed, 2026-10-09.

This follow-up fixes two concrete sources of friction observed in
[report 156](156-state-sensitive-io-maintenance.md). The conventional validation
host no longer requires the NuGet vulnerability feed to be available, and Flow/2
runtime help now provides an executable enum-returning library example.
No fresh participant comparison was run. These are verified tooling changes,
not evidence that agent efficacy has improved.

## Fixed conventional validation

Both host-selected `dotnet build` and `dotnet run` commands pass
`-p:NuGetAudit=false`. The validation response logs the exact command arguments.
Agents still cannot select arbitrary executables, projects or command arguments.
Normal package restoration remains enabled: this suppresses the vulnerability
lookup, not all network activity or dependency resolution.

The integration regression sets the project property to `NuGetAudit=true` and
has an MSBuild guard that rejects restore/build unless the effective value is
false. Both actual build and run validations pass and report the override.
Existing credential filtering, output caps, failure exits, timeouts and path
checks remain covered by the conventional suite.

The freshly rebuilt conventional CLI also validates a disposable copy of the
saved report-156 F# implementation and captures `SelfTests passed.` This replay
verifies the host fix; it does not change the original participant's failed
validation attempts or its frozen runtime and traces.

## Executable enum help

Flow/2 Define and Examples help now include `tutorial-enum-tests`, comprising a
three-case `TutorialSign` enum, `tutorial.classify`, and three own tests. Runtime
help states that payload-free cases are zero-argument constructors such as
`TutorialSign.positive()`, rather than record properties. Constructed expected
values use `=> value TutorialSign.positive()`.

The runtime regression retrieves the help-returned source and requests, checks
that they agree, executes define/test/library commit, reruns the three tests,
and reloads the persisted tests into a fresh engine. Library qualification
therefore checks the example's own branch/instruction and enum-return coverage.
The default Flow/1 help inventory remains unchanged. The syntax guide includes
the constructor/record-property distinction and expected-value spelling.

## Local validation

Accepted Release builds completed with zero warnings and errors:

- Conventional tools: 218 assertions passed.
- Flow runtime: 33 groups, 1,208 assertions passed.
- Discovery: 140 assertions passed.
- Harness: 249 assertions passed.
- Rebuilt conventional CLI: saved implementation validation passed, with the
  audit override visible in the JSON response.
- Diff checks passed. CI remains manual-only.

The first implementation build encountered an F# nested-indexer syntax error in
the new conventional test; an intermediate binding fixed it before the accepted
build. The first harness attempt used the default temporary location and failed
with `ROLLBACK_FAILED: STORAGE_ACCESS_DENIED`. After setting TEMP/TMP to the
workspace-local test directory, the same freshly built runner passed. Neither
failure is a fresh agent-efficacy observation.

No parser, type/effect semantics, semantic IR, native layout or arena policy
changed. The focused suites exercise the affected validation and help contracts;
the complete 37-check native/business gate was not repeated for this change.
UTF-8 transport friction seen in report 156 remains a separate unresolved item.

The [evidence manifest](evidence/157-audit-independent-validation-and-enum-help/manifest.json)
records 19 archive entries, 11,355 bytes, and SHA-256
`b61aa7371ab9f398c6887d5b91b3615f60da4543e18c2110d6bb30ea6d5f26d3`.
