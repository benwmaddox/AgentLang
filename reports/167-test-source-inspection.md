# Inspecting existing test bodies

Status: implementation, focused validation and all 37 full local Release checks
complete. Evidence archive verified. No new agent experiment.

## Why this change

[Report 166](166-handoff-signature-maintenance-agent-pair.md) exposed two distinct
issues. The language participant listed prior cases but could not retrieve one
through `source`; it eventually used durable history. It also weakened two prior
test scenarios while passing all coverage gates and independent behavior checks.
The conventional participant preserved its prior assertions. Easier access to
current test bodies addresses the demonstrated discovery friction; it does not
establish that the lost assertions were caused by the interface.

The existing `tests` operation now has an opt-in source view and a single-case
selector. Name-only listing stays compact. The view returns current authored
case source rather than its lowered Stack projection. Where a test-file replaces
an I/O dependency, it includes the enclosing setup. Staged and temporary cases
are available without durable history; persisted cases retain their source on
reload. Inspection does not run the target, tests or effect providers.

## Request contract

```json
{"op":"tests","word":"settings.load"}
{"op":"tests","word":"settings.load","includeSource":true,"caseName":"enabled"}
```

The default remains sorted case names. Source rows carry `word`, `name`,
`source`, `sourceHash`, `frontend` and `syntaxVersion`. Wrapped cases also carry
`testFile` with `scope`, full `source`, its `sourceHash` and the named override
sources. Hashes describe the respective exact UTF-8 source text, so a nested
case hash is distinct from its shared enclosing wrapper reference. Selectors and
the boolean flag must be checked; nonexistent owners/cases return diagnostics
instead of an apparently successful empty selection. Unknown extra fields keep
the generic protocol's existing behavior. No new operation, IR instruction,
storage format or test execution mode was added. Successful source retrieval is
recorded as an inspection in an active task log; it is not counted as execution.

## Validation

Fresh serial Release rebuilds of Core, CLI and the runtime test executable pass
with zero warnings/errors. The focused runtime suite passes 40 groups and 1,591
assertions. One new group checks default names, case selection, exact standalone
and wrapped Flow source, separate case/wrapper hashes, override source, temporary
staging and abort, durable reload, an active persistent wrapper replacement and
rollback, Stack authored text, malformed selectors and read-only behavior.
Dictionary definitions, stored authority, test results and coverage remain
unchanged by inspection; task counters show no tests or effects.

A CLI smoke check on a disposable copy of report 166's actual pre-change seed
retrieves all five authored handoff cases, including `missing-old-precedes-creation`
and `success`. Returned hashes match the exact source bytes. All 276 seed files
and all copied files remain unchanged; no test is executed. This confirms source
retrieval for the previously troublesome case, not improved agent behavior.

The development log preserves an invalid `dotnet build --target Rebuild` attempt,
corrected to `-t:Rebuild`, and three F# method-argument parenthesis errors in the
new regression fixture, followed by the successful rebuild and test run. These
are implementation/validation failures, not new participant outcomes.

The first full gate exposed three acceptance consumers that expected an empty
test list for an owner removed by publication projection, task abort or temporary
cleanup. The new handler reports `NAME_UNKNOWN_WORD`; that attempt was interrupted
after the confirmed failures and is not a passing gate. The consumers now check
the diagnostic and run the complete remaining test projection, with expected
counts of two, one and zero, to retain proof that discarded metadata is gone.
The updated acceptance suite passes all 35 groups and 642 assertions. The fresh
full local gate passes all 37 checks with workspace-local TEMP/TMP and package
auditing disabled. The business-policy preflight passes 98 checks across 30
independent control outcomes, including rejection of no-op, trim, rate, rounding
and overflow faults in each environment. The initial failure record and
successful acceptance log are retained.

The [verified evidence archive](evidence/167-test-source-inspection/evidence.zip)
contains the focused log, initial failure record, acceptance rerun, final gate
and subordinate reports, post-build source pin and historical-seed smoke files.
The archive verifies CRC and each entry's SHA-256 against its
[manifest](evidence/167-test-source-inspection/archive.json). Its size is
1,621,419 bytes and SHA-256 is
`881295cf3fe46aa0bbb4dc3ef8edc88aef57990e764c4125cc52090399be6e25`.
All four changed implementation/test files match their post-build source pin.

A read-only Luna/max review found no concrete defect in source authority,
attachment lifecycle, rollback or effect isolation. It identified ignored unknown
request fields as a selector-typo risk; this milestone intentionally retains the
generic protocol's permissive extra-field behavior. Supported selectors and
flags are validated. The review ran no builds or tests and was not a full audit
of the runtime or filesystem implementation.

## Efficacy interpretation

The cumulative evidence remains mixed. [Report 163](163-atomic-subscription-handoff.md)
shows both fresh agents voluntarily composing retained cancellation/creation
operations and passing all 33 independent cases. Report 166 shows successful
maintenance of those actual implementations and callers in both environments,
but incomplete language-side test preservation. Conversely,
[report 159](159-retained-io-vocabulary-follow-on.md) shows the language agent
discovering a retained I/O helper and duplicating its behavior, while F# reuses it.
The dictionary makes composition possible; it does not guarantee voluntary reuse
or more reliable edits.

Keep strong types, effects, attached tests and library gates. Continue checking
complete behavior and collateral preservation independently. Do not convert
passing coverage into a correctness claim, combine differing task denominators
into a success rate, or infer token savings from protocol bytes. Smaller-context
performance and general comparative reliability remain unproven. Native/arena
results are a separate engineering track.
