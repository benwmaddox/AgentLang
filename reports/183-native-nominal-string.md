# 183 — Native nominal String wrappers

Status: accepted local validation, 2026-10-10.

The owning native backend supports String refinements with frozen pure
predicates. This slice also admits a String-backed nominal type with no predicate using the same UTF-16 payload layout, exact host
type names, nominal TypeIds and owner ranges. A nominal type is distinct from
its base and other wrappers; an absent predicate must remain absent.

The [acceptance plan](../docs/NATIVE-NOMINALS-IMPLEMENTATION.md#next-slice-unvalidated-nominal-string-planned-after-checkpoint-182)
requires O0/O2 checks of nominal identity, host rejection, UTF-16 bytes, nested
values and mailbox admission. No allocator, source syntax, capability, dependency
or ABI/layout version changed. Bool/Float wrappers and List remain
explicit native gaps. Semantic IR remains the executable authority.

All local gates passed. Independent review found no blocker in the frozen
primary and companion changes. The [evidence index](evidence/183-native-nominal-string/archive.json)
and [archive](evidence/183-native-nominal-string/evidence.zip) retain tested source
pins, terminal results and preparation failures.

| Local validation | Accepted result |
| --- | --- |
| Focused nominal String, O0/O2 | 41 assertions |
| Full LLVM suite | 694 assertions |
| Independent native runner | 1,454 checks, including 34 nominal String cases |
| Fresh native script verifier | 50 checks; all 47 source inputs unchanged |
| Full mailbox regression | 2,255 checks, including prior refined-value cases |
| Mailbox memory-policy regression | 741 checks |
| Host-side real-I/O regression | 207,022 repeated checks |
| Full local Release gate | 37/37 checks |

The new runner matrix has 17 cases at each of O0/O2. It covers exact host identity,
bare/wrong-nominal rejection, UTF-16 code units including NUL and isolated
surrogates, nested records/Option/Result, and owner-range preservation. Wrapping
and unwrapping retag descriptors with zero added payload copy or movement;
explicit `dup` and record construction keep their existing accounting. The raw
mailbox case establishes import/round-trip behavior, not a complete nominal-String
controller lifecycle.

Regression counts include repeated observations, not distinct new cases. This
engineering slice adds no external-agent outcome, selects no arena policy and
establishes no service performance or cybersecurity certification.

Fresh builds use isolated artifacts and workspace temporary storage. The native
script, mailbox, policy and real-I/O gates run through their existing local
verifiers. The full language command is:

```powershell
pwsh -NoProfile -File scripts/Validate.ps1 -Configuration Release -SerialBuild -SkipPackageAudit -ReportPath .agentlang/nominal-string-183/full-validation.json
```

## Retained preparation attempts

Static review found that the first mailbox fixture consumed its retained State
while projecting a field; the fixture now preserves State before constructing
Continuation. A fresh build rejected test access to an opaque verified-program
field, corrected through the inspection API. The first build wrapper omitted its
yielded session handle; a later process inspection found no live dotnet process.
Its partial log is retained as an indeterminate attempt, not a passing build.
Subsequent builds retain their handles and terminal exit results.

The first focused run also rejected a resume fixture that dropped State along
with completion and continuation. The fixture now retains State to satisfy the
fixed lifecycle signature. These are test-preparation defects, not evidence of
successful native execution; their failed logs remain part of the record.

Later focused attempts exposed an incomplete copy oracle: the callback expected
only the eight-byte Continuation marker construction. Independent source review
also counts a sixteen-byte `dup` of the owning State, so the expected deep-copy
total is 24 bytes, separate from the 32-byte external import. Wrap/unwrap itself
adds no payload copy. The corrected focused run passes; the earlier failed
counter assertions are retained.

The independent runner's first full attempt used interpreter roots after their
disposal and reversed the expected IDs of two nominal wrappers. Its fixtures
now keep roots alive through comparison. Independent compiler-source review
derives the ID ordering from ordinal nominal-name ordering, rather than treating
observed native output as its own oracle. The second attempt reached the new
matrix and found an input factory returning a base String where its identity
entry required TextTag; the factory now uses the typed constructor. These are
runner preparation failures, with failed evidence retained. Corrected attempt 4
passes the full matrix. Review also found one extra padding byte in a ResultError fixture:
eight tag bytes plus a 24-byte String extent require 32 bytes total. A run that
had loaded the old fixture was interrupted and excluded. The corrected fixtures
were checked against their UTF-16 lengths and aligned extents before restarting.

The first real-I/O regression attempt could not connect to its local loopback
provider inside the sandbox (`10013`). An authorized host-side retry passed the
same test without changing language capabilities or using an external endpoint.
Package auditing is disabled for these local gates; this is not a dependency
security audit.
