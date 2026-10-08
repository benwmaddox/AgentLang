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

The full local Debug gate completed with **35 of 37 checks passing on its first
run**. Its two failures were corrected and individually rerun; both pass. The
report retains the failed initial run rather than presenting it as a single
all-green full-gate invocation.

- Fresh full-solution build: zero warnings and errors.
- Flow: 1,071 assertions; final Flow Runtime: 27 groups / 875 assertions.
- IR verifier: 132 assertions; IR interpreter: 58; LLVM: 443.
- Storage: 16 groups / 368 assertions, including exact enum path encoding,
  malformed indexes, Flow/1 rejection and unchanged authority after rejection.
- Value inspection: **48 assertions after correction**. The new test had queried
  a nonexistent nested `type` field on a root enum DTO; it now checks the schema
  through a typed list's `elementType`. A negative nominal-name test now supplies
  its expected type through that list. No runtime behavior was changed for this fix.
- Script-only Flow call bindings: **31 assertions after correction**. Two context
  literals needed `Enums = Map.empty`; the exact failed command then passed.
- Native-mailbox adapter: a matching empty-catalog update, built with fresh
  isolated dependencies, zero warnings/errors.
- Existing business-language regression: 6,319 assertions, 110 attached tests,
  27 examples, 36 words and 27 types persisted/reloaded. Historical fixtures were
  not edited.
- Deterministic policy preflight: **98 checks / 30 outcomes passed** across
  growing, flat and conventional modes, including wrong-solution controls.
- All ten frozen Release artifact hashes remained unchanged. Diff checks passed.

The full run used the working tree based on `5e7bcc7`; the implementation was
locally checkpointed as `404a7a4` while the lengthy preflight was still executing
its already-built Debug binaries. A final defensive document-shape check and
additional library-guard tests were validated in fresh isolated Flow Runtime
artifacts. The final inspection correction changes tests only. The broad gate
was not repeated after these bounded corrections. The next effect-assertion
implementation is outside this milestone and its validation.

Reproduce the broad gate with:

```powershell
./scripts/Validate.ps1 -Configuration Debug -ReportPath .agentlang/enum-validation.json
```

The corrected script command was:

```powershell
dotnet fsi --exec --reference:src/AgentLang.Core/bin/Debug/net9.0/AgentLang.Core.dll scripts/Verify-FlowCallBindings.fsx
```

Focused fresh builds use isolated `--artifacts-path` directories to preserve
frozen binaries. [The evidence index](evidence/112-closed-domain-states/index.json)
records hashes for full and focused logs, independent reviews, a compact policy
summary, and a ZIP retaining the exact 14 MB raw policy result. The pending
finite-coverage plan is explicitly planning evidence, not delivered behavior.

## Next work

Implement revision-bound finite observations for library parameters and returns,
including branchless functions, with expectation evaluation excluded. Add authored
effect-count assertions so report 111's extra-write control fails the library's
own tests. Follow with complete-record construction invariants and a new bounded
agent comparison. These are pending requirements, not conclusions about efficacy.

See [enum syntax](../docs/CLOSED-ENUMS.md) and
[structural requirements](../docs/STRUCTURAL-CORRECTNESS.md).
