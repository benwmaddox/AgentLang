# 142 â€” Library qualification includes authored dependencies

Status: the frozen external-agent probe passes independent acceptance. Local
validation is complete after repairing one legacy acceptance fixture. The original
37-check gate had 36 passing checks and one failed acceptance command. That exact
command passes after the fixture repair (35 groups / 632 assertions). The final
focused runtime suite passes 31 groups / 1,106 assertions; LLVM conformance passes
476 assertions. Historical business-policy preflight passes 98 checks across
30 independent outcomes. The original failed gate and passing focused rerun are
retained separately; this is not a claim that the original full script was green.

Report 141 established feasible shared-rule maintenance in both environments,
without a language reliability lead. This milestone closes an already-approved
structural requirement rather than adding another easy matched task: a library
function must not obtain library status while depending on an ordinary authored
helper. Previously the dependency check applied only to enum-bearing helpers.

The runtime now checks the complete authored dependency closure, including static
collection callbacks. Trusted primitives and generated type operations remain
allowed. Ordinary functions retain their lighter gate. The deterministic
`LIBRARY_DEPENDENCY_NOT_QUALIFIED` diagnostic identifies the library target and
the unqualified helper and explains the qualification step.

Draft candidate dependencies remain stageable so a helper and caller can be
edited together. Publication checks the selected final dictionary before durable
storage, and every library candidate must also pass its own tests and coverage.
Affected persisted callers are checked against the durable proposed snapshot,
including when a separate staged caller temporarily removes the dependency.
Load, rename and other existing requalification paths use the same rule. This
reuses existing dependency and revision machinery rather than adding a separate
qualification cache.

This is an intentional prototype compatibility change. A saved library that
uses an ordinary helper must qualify that helper before the new runtime accepts
it. Historical study archives and pinned runtimes remain unchanged. Explicit
module membership, cross-module access and entry-point rules are still pending;
the implementation does not infer modules from dotted names.

## Controls and validation

The old report-141 CLI accepts a library integer wrapper over an ordinary helper.
It also accepts the proposed discount wrapper while the policy helper has only
one own test. These are successful pre-change controls, not hypothetical gaps.

With a fresh changed CLI:

- Wrapper-only library promotion fails with `LIBRARY_DEPENDENCY_NOT_QUALIFIED`.
- Group qualification with incomplete helper tests fails with
  `LIBRARY_COVERAGE_INCOMPLETE`.
- Adding the helper's missing non-premium test permits atomic qualification;
  all four attached tests pass.
- A false-branch mutation returning 99 instead of 0 passes the seed helper's
  single test but fails the completed helper's own non-premium assertion.

The focused suite exercises direct and indirect dependencies, callbacks,
ordinary composition, trusted/generated calls, group qualification and grouped
replacement, failed-replacement durability, and persisted-load requalification.
An independent read-only review found no concrete staging/publication bypass
and requested the staged-caller masking regression, which is included.

The fresh CLI build has zero warnings and errors. The latest focused test run
adds an explicit passing execution of the staged caller before its hidden
durable dependency rejects the helper-only commit. That final test-only addition
was validated separately after the full gate's initial build; runtime and help
sources did not change during the full gate. The probe scorer accepts the
identity-preserving qualified control and rejects the seed's qualification and
mutation protection while confirming that both already have correct outputs.

Coordinator setup mistakes are retained separately: an initial infix-addition
example failed parsing, a first replacement request omitted its replacement
metadata, and an initial isolated test runner needed an explicit CLI path after
its artifacts were relocated. None is an agent outcome or evidence that the
qualification gate passed or failed. The successful controls use corrected
requests and preserve the intended source/test identities.

The scorer's first setup attempt also encountered a Windows access error from
strict path resolution before starting a runtime process. Non-strict path
resolution followed by explicit file/directory checks works with the same DLL;
the corrected scorer's control runs complete successfully. This is coordinator
setup, not an agent observation.

## External-agent result

One fresh Luna/max subagent received the frozen task, a compact protocol primer,
and the seeded project. It was asked to qualify `discount.rate` as a library
while preserving both functions, their signatures, behavior, effects, inherited
tests and the wrapper's call to `discount.internal-rate`. The seed already had
correct behavior and three passing tests; the helper lacked its own false-case
test. This was a guided qualification task, not an application feature repair.

The participant completed 29 broker exchanges, added the helper's `standard`
test, qualified both functions, passed 4/4 tests and finalized the task and host.
Independent scoring passes all nine dimensions: clean execution, attached tests,
each function's current library qualification and coverage, all four Bool-input
behavior cases, no active task, stable identity and inherited tests, effective
mutation protection, and unchanged submission during grading. The new test
rejects the helper's false-branch return changed from 0 to 99 with an exact
`TEST_ASSERTION_FAILED`; the seed's helper test accepts that same defect.

The trace contains one failed response: exchange 8 sent an unsupported `frontend`
field to `help`, producing `HELP_INVALID_ARGUMENT`. The corrected request succeeds.
There is no `LIBRARY_DEPENDENCY_NOT_QUALIFIED` response: this is successful guided
qualification, not evidence of recovery from that diagnostic. The independent
audit verifies all 30 frozen hashes, unchanged function source hashes, preservation
of all inherited tests and the wrapper's original helper call.
The runtime and scorer were frozen before dispatch. Focused tests, LLVM checks,
and positive/negative probe controls passed first; the slow historical preflight
was allowed to finish concurrently. The subsequent acceptance-fixture repair
changes test setup only, not the frozen production runtime.

## Efficacy boundary

These controls prove a specific enforceable composition rule and a meaningful
test gap. The external probe supports the feasibility of discovering and meeting
the stronger library contract while retaining function composition. It does not
prove better application logic, correct test expectations, a causal improvement
from the diagnostic, or superiority over F#. There is one guided language-only
participant and no matched comparison arm. The original output behavior was
already correct; the demonstrated gain is own-function regression protection
and enforceable qualification of the dependency closure.

Explicit modules and scoped dictionary overrides remain unfinished. See the
[validation plan](../docs/LIBRARY-CLOSURE-VALIDATION.md) for the next proposed
matched task, which must use reachable application behavior and assess effective
regression protection rather than require duplicate tests.

## Retained evidence

The [results archive](evidence/142-library-dependency-qualification/results.zip)
contains the frozen task and scorer, baseline/positive/negative controls, final
participant project, raw broker trace, independent grading and mutation results,
hash audit, local validation logs, original failed gate and focused repair,
and changed source snapshots. Build binaries and temporary directories are
excluded. All 254 entries were independently verified against both their source
bytes and the [SHA-256 index](evidence/142-library-dependency-qualification/results-index.json).
The archive is 1,837,008 bytes, SHA-256
`c8d2c1f72505a6235472a78297ded17c727aec57b13d25fa212df1491532d649`.

The original full gate has 37 checks; its only failure was the container library
coverage fixture reaching the new dependency gate before its intended coverage
assertion. The corrected setup first qualifies its callbacks, including the
filter callback's own false-case test, and keeps all original owner-coverage
negative assertions. The fresh build and exact failed command pass. Final runtime
coverage tests include seven assertions added after the full gate's build;
production runtime/help source did not change during that gate. The retained
`final-validation-audit.json` explains this validation sequence explicitly.

The working tree based on `841af057c5c13445dfa28a58132545f5eaa85282`
was validated locally with:

```powershell
pwsh -NoProfile -File scripts/Validate.ps1 -Configuration Release -SerialBuild -SkipPackageAudit -ReportPath D:/code/AgentLang/.agentlang/library-closure-142/full-validation.json
dotnet run --no-build --configuration Release --project tests/AgentLang.Acceptance
dotnet run --no-build --configuration Release --project tests/AgentLang.Llvm.Tests
```

The acceptance command followed a fresh Release build of the corrected fixture.
The final focused runtime run used isolated build artifacts and an explicit
`AGENTLANG_TEST_CLI` path. NuGet vulnerability auditing was skipped explicitly;
these checks do not certify current package vulnerabilities. CI remains manual-only.
