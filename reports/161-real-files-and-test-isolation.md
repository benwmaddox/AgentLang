# Real files and isolated library tests

Status: all 37 full local Release checks pass, alongside focused runtime, CLI and
script checks. Source-defined test-file replacements are not
implemented by this milestone. This is product engineering, not an efficacy trial.

## Boundary now implemented

Normal Engine and CLI execution uses real project-rooted UTF-8 files. Explicit
`FileSystemMode.Virtual` or CLI `--filesystem virtual` selects a simulated session.
Every attached test gets a fresh empty virtual filesystem, regardless of normal
mode, for setup and its target invocation. Expected expressions also get isolated
virtual state. Library publication, replacement gates and load-time requalification
use the same test runner. Normal `example` execution uses the configured provider.
This is the managed Engine/CLI provider boundary, not a new LLVM filesystem backend.

Test isolation no longer bypasses capability checks. The optional Engine
`testCapabilities` and CLI `--test-allow` explicitly configure permissions for
isolated tests; absent those settings, tests use normal grants. For example,
`--allow fs.read --test-allow fs.read,fs.write` permits virtual fixture setup while
keeping production execution read-only. The original function effects remain
checked. Test grants cannot authorize a real production write.

Real paths must be relative and remain beneath the configured project directory.
Existing links/reparse points below the host-configured root are rejected on a
best-effort basis. The configured root itself is trusted host configuration; its
ancestors are not inspected. These checks are not a race-proof filesystem sandbox.
Reads accept UTF-8 with an optional UTF-8 BOM,
and writes emit strict UTF-8 without a BOM. File errors have structured diagnostic
codes. Snapshots and task abort retain their virtual-state behavior; they do not
undo real external writes.

## Focused evidence

- Fresh Core and CLI Release builds pass.
- Flow runtime: 33 groups / 1,208 assertions.
- Stack acceptance: 35 groups / 633 assertions.
- Harness: 249 assertions.
- CLI: 12 groups / 188 assertions, including the new real/virtual boundary group.
- V1 broker verifier: 34 checks; V2: 37 checks; persistence projection: 99 checks.
- IR verifier: 329 checks across 24 sessions; same-binary infrastructure check,
  not interpreter/native parity.

The CLI group reads exact Unicode/CRLF contents from a host file and creates a
real file under the default mode. An explicit virtual session writes no host file.
Attached tests shadow an existing physical file with virtual fixture contents;
another test sees fresh empty state. Library publication reruns those tests
without changing the host file, and normal execution subsequently reads its
original contents. A read-only production session can reload and test the library
with separate test grants, yet its attempted real write is denied. Tests without
grants also fail. Invalid UTF-8 and escaping paths are rejected. An invalid Unicode
source literal fails in the parser before writing, preserving existing contents;
that case is not claimed as coverage of the provider's encoder-failure branch.

Existing simulation clients now select Virtual explicitly: 119 test Engine
constructions and one benchmark construction. Four effect-count fixture Engines
receive explicit read/write grants that their actual tests need; intentional
denial fixtures retain empty grants. Current scientific broker and CLI scripts
now pin simulation instead of depending on the former default.

Logs are preserved under `.agentlang/filesystem-test-boundary/`. Initial new test
builds exposed F# indexing and JSON helper inference errors. Initial denial probes
used a committed library without its test grants and failed at requalification,
then used the wrong expected capability error label. The corrected probes check
actual `CAPABILITY_DENIED` responses on a fresh candidate and the separate-grant
reload behavior. A later regression run failed because a concurrent Rebuild
removed the CLI runtimeconfig while the test executable was launching processes.
This coordinator scheduling failure is retained; a serialized fresh build and
rerun pass. A refresh build's three locked-DLL warnings remain in its original log.
Final review also prompted real existence and missing-read assertions.

The first broker verifier extension incorrectly constructed a PowerShell argument
array; the corrected negative fixture proves reserved-provider option rejection.
The IR verifier exposed a preexisting stale library fixture in both Real and
Virtual modes. Its helper had only a true-return test, and the commits selected
the caller while leaving the helper unqualified. The fixture now tests the
helper's false return itself and commits the candidate group. The original
expected caller coverage rejection, eventual publication and reload assertions
remain unchanged. Failed and accepted reports are retained.

## Still required

Provider simulation is separate from replacing a source-defined dependency.
The user requires replacements scoped to test files. The proposed shared
test-file context must survive save/reload, reach nested calls and callbacks,
preserve signatures/effects/capabilities, restore production dispatch on every
exit, and never award original-function coverage to a fake. See
[test-file dependencies](../docs/TEST-FILE-DEPENDENCIES.md).

The current durable unit is an individual attachment. File scope requires parsing
and retaining shared source context, selecting cases on reload, and keeping
binding metadata and rewrites consistent. A per-case-only mock is not claimed as
completion of that requirement. The efficacy handoff trial has provisional seeds
and an independent bounded-cell model, but no participant dispatch or results.
Its runtime must be rebuilt and controls verified after these changes.

Full Release validation passed on 2026-10-09 in 43 minutes, including the business
preflight's 98 checks across 30 independent outcomes. CI remains manual-only.
The filesystem candidate was frozen in Git's index before subsequent test-file
overlay edits began. Those later edits stayed unstaged and no shared binaries
were rebuilt during the gate. The evidence archive reads the frozen index;
its code-index guard verifies that the candidate remained unchanged. The full
gate's final whitespace checks also passed on the current working tree.

The full Release command is `Validate.ps1 -Configuration Release -SerialBuild
-SkipPackageAudit -ReportPath .agentlang/filesystem-test-boundary/full-validation-001.json`.
Package audit is deliberately skipped; no dependency-security result is claimed.

## Frozen evidence

[Evidence archive](evidence/161-real-files-and-test-isolation/evidence.zip) and
[archive metadata](evidence/161-real-files-and-test-isolation/archive.json) preserve
428 entries in 2,602,283 bytes, including failed and accepted runs, review,
controlled script outputs, and the frozen candidate sources. Every archived entry
and its source/index bytes were hash-checked. Build binaries are excluded.
Archive SHA-256: 06b962470e29854beac1db12ab6f33c38b8de692f140a1298fee0c616b4b976b.
