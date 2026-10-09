# 152 — Occupied periods in the business domain

Status: implemented; local validation resolved after a focused rerun, 2026-10-09.

## Research finding applied

[Report 143](143-subscription-overlap-comparison.md) exposed an empty-occupancy
mistake in an agent submission despite passing authored tests and full library
coverage. The tracked business application did not yet enforce subscription
overlap. This change integrates that policy into the application and its F#
reference; it does not rewrite or repair the archived participant submission.

An `OccupiedPeriod` can only contain a nonempty half-open interval. Deriving a
subscription's occupancy returns `Option<OccupiedPeriod>`, making an empty
period explicit. Active occupancy ends at expiry; cancelled occupancy ends at
the earlier of cancellation and expiry. Cancellation at start yields `None`.
Only subscriptions for the same customer and product conflict, and touching
endpoints remain valid. Overlap rejection follows the existing ID, term, date,
customer and product checks, preserving their error precedence.

Both implementations use existing types and validation facilities. There are no
new trusted primitives or persisted Store fields. The language fixture now uses
Flow/2 functions, properties, equality and default pure effects; the integration
loader respects each fixture file's authored syntax version.

## Validation and limitations

The independent integration oracle enumerates occupied 100-nanosecond cells,
rather than restating the implementation's endpoint comparison. It covers 25
constructor pairs, 100 pairs of valid intervals and 30 start requests over five
active/cancelled states. It checks both languages against independently expected
occupancy, compares full successful Store projections, and checks preservation
of the input Store after rejection. Attached tests additionally cover ownership,
product identity, offsets and validation precedence.

The final focused transition suite passes ten groups and 8,644 assertions,
including 184 attached baseline tests, 47 examples, independent occupancy checks,
library qualification, persistence/reload and both existing Flow/2 extensions.
The F# reference passes eight groups and 804 assertions on its final source.
The first aggregate library promotion rejects `subscription.start`: a second
date-validation branch is unreachable after the first check. The correction
uses one checked period construction and carries its successful value forward;
coverage requirements remain unchanged. The corrected implementation passes
aggregate library promotion and reload. The standalone CLI also passes the
aggregate library commit with 179 tests after excluding the two existing
project-only email functions, without changing their maturity requirements.
Static independent review found no further correctness blocker.

The full local Release gate completed 37 checks: 36 passed, including the
business-policy preflight's 98 checks over 30 independent control outcomes.
Its sole failure was the extension-import version mistake described below. The
fresh final transition build and complete rerun pass all ten groups and 8,644
assertions. The original full-gate report remains marked failed in the evidence;
it is not rewritten as an uninterrupted green run. Package auditing was skipped
for this local run; these results do not establish a dependency-security audit.

Test-harness failures are retained as well: an expression-only evaluator rejected
an inline statement block, and the replacement temporary Flow/1 function first
omitted its required effects declaration. A loader edit also accidentally removed
the Flow/2 declaration from two existing extension imports; their explicit
versions were restored after the post-persistence run caught it. These are
coordinator implementation errors, not observations from a new independent
agent trial.

The current experiment preparer is updated for 59 authored functions, 33 types,
184 tests and 47 examples, with 57 library functions and two project functions.
It defines the subscription file as Flow/2, and extracts the two project-only
email functions from their actual owning source file. Archived experiment
submissions and frozen protocols are unchanged.
The isolated growing-seed preparer passes definition, full tests, library
qualification, project-only restoration and fresh-process metadata checks.
Historical retention verifiers pin hashes of the shared preparer and fixtures:
reproduce those studies from their original checkout/archive, not this modified
working tree. Their hash checks are intentionally not relaxed. Seed reuse does
not yet fingerprint the per-file syntax selection separately from source bytes;
all affected source bytes changed here, so existing seeds are invalidated.

This checkpoint demonstrates integration of a structural domain invariant. It
does not show that an unaided agent invents the abstraction or that AgentLang
outperforms F# on reliability. Both implementations receive the same design.
Coverage still cannot prove the business policy itself; independent expected
behavior remains necessary.
This change does not add a global Store/import invariant. A cancelled record
without a cancellation timestamp follows a defensive absence path;
the tested transition policy concerns states reachable through normal creation
and cancellation. `OccupiedPeriod` validates interval shape, not every possible
subscription record or imported Store.

## Evidence

The [evidence archive](evidence/152-occupied-period-domain/evidence.zip) preserves
build/run logs, failed attempts, standalone CLI requests and replies, final
transition output, full validation and preflight results, source snapshots,
and independent review. Its manifest hashes each entry, and
[archive metadata](evidence/152-occupied-period-domain/archive.json) records the
verified archive hash. Temporary runtime stores and compiled binaries are omitted.
The implementation started from `a89e48447ff35068b2799382e9450a0c576a7da9`.

Principal validation commands were:

```powershell
pwsh -NoProfile -File scripts/Validate.ps1 -Configuration Release -SerialBuild -SkipPackageAudit -ReportPath .agentlang/occupied-period-152/validation.json
dotnet build tests/AgentLang.Business.Transitions.Tests -c Release -m:1 -p:NuGetAudit=false
dotnet tests/AgentLang.Business.Transitions.Tests/bin/Release/net9.0/AgentLang.Business.Transitions.Tests.dll --evidence .agentlang/occupied-period-152/integration-final-006.json
```

## Runtime direction remains open

The user is considering specialized typed mailboxes/dataflow instead of a full
actor system. Smalltalk-style inspection and scoped dictionary replacement can
be evaluated separately from actor identity, spawning and supervision. Whether
a suspended handler permits other messages to run is also separate from whether
its transient arena is retained or returned. This checkpoint selects neither a
full actor framework nor a final suspension/memory policy.
