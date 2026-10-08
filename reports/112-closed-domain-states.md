# Closed domain states and exhaustive enum decisions

This milestone adds payload-free nominal enums to Flow/2 project code. It is an
implementation checkpoint for the approved structural correctness requirements,
not a new agent-efficacy result. Strict finite-value library coverage and native
enum execution remain pending; enum-bearing library qualification is blocked.

## Behavior

An `enum RenewalState { case pending; case renewed; case cancelled; }` declaration
creates nominal cases such as `RenewalState::pending()`. A match must name every
case exactly once. Unknown, missing and duplicate cases fail before execution;
arm outputs and effects must agree with the enclosing function contract.
Different enum types cannot be interchanged through matching or equality.

The authoritative typed IR retains enum case tables, checked constructors and a
dedicated match operation. Independent verifier checks reject malformed case
sets, nominal identities, generated operation metadata and incompatible joins.
Interpreter coverage reports the actual authored case labels. Value inspection
and dictionary metadata retain enum identity and cases.

Flow/2 source and call bindings persist through the existing version-3 manifest.
Constructor calls and calls nested inside match arms retain their binding paths.
Flow/1 rejects enum syntax and enum-specific stored paths. This extends supported
Flow/2 semantics; it does not promise that an older runtime can load new enum
projects. Existing source formats and frozen research fixtures remain unchanged.
Committed type definitions remain immutable; changing a case set is not yet a
supported migration operation.

## Library boundary

`LIBRARY_FINITE_COVERAGE_UNSUPPORTED` prevents enum-bearing authored functions
from qualifying as library functions before the required finite input/output
observations exist. The guard follows reachable helpers, explicit container type
arguments, nested record fields, generated constructors and enum matches. An
unrelated enum declaration does not disqualify enum-free code.

This temporary restriction does not implement the broader Bool, Option/Result or
module qualification requirements. Own instruction and branch coverage continues
to mean structural coverage under the existing contract. The stronger finite
library gate remains a separate implementation step.

## Validation

Local checkpoint; publication awaits the in-progress policy preflight.

Focused Debug results: Flow 1,071 assertions; Flow Runtime 27 groups / 875
assertions; IR verifier 132; IR interpreter 58; Storage 16 groups / 368.
The fresh full-solution build had zero warnings/errors. Its script-only
call-binding probe initially failed because two compiler-context literals lacked
the new enum catalog; after correction, the exact command passed 31 assertions.
The native-mailbox adapter also received the empty catalog and built with fresh
isolated dependencies with zero warnings/errors.

The full gate is still running its final deterministic business-policy preflight.
The final report will retain that run, its known corrected script failure, and
the focused tail validation separately; it will not claim a single all-green
full-gate invocation. The tail changes are a defensive document-shape check and
library guard tests, covered by the final Flow Runtime run above.

## Next work

Implement revision-bound finite observations for library parameters and returns,
including branchless functions, with expectation evaluation excluded. Add authored
effect-count assertions so report 111's extra-write control fails the library's
own tests. Follow with complete-record construction invariants and a new bounded
agent comparison. These are pending requirements, not conclusions about efficacy.

See [enum syntax](../docs/CLOSED-ENUMS.md) and
[structural requirements](../docs/STRUCTURAL-CORRECTNESS.md).
