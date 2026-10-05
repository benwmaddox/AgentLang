# Structured values and expression test expectations

## Scope

This checkpoint adds typed value-expression expectations to tests and returns
bounded structured values for those test results. It does not change the
existing literal or expected-runtime-error response fields. Expected
expressions remain statically checked, pure, and tied to the same verified
program snapshot as the test body. Their execution does not contribute
coverage to the tested word.

## Result JSON

Expression-expectation results retain the existing display-oriented `actual`,
`expected`, and `expectedType` fields and add `expectedKind: "value-expression"`.
When values are available, `actualStructured` and `expectedStructured` use the
`ValueInspection.toData` envelope (`formatVersion` and `values`) from the exact
runtime snapshot. Literal and expected-error results keep their prior shape.

Structured observation is optional and separately bounded. If an inspector
resource limit is reached, the corresponding structured field is JSON `null`
and its `...Error` sibling reports the stable diagnostic code and message (and
the diagnostic's expected/actual details). That observation limit does not
change whether the language test passed and does not reject a candidate during
commit. Non-limit consistency diagnostics are checked before commit or rename
publication so result serialization cannot be the first operation to fail
after a durable transition.

The `eval` `structured` argument is a strict JSON boolean. An invalid value is
rejected before expression execution. For valid structured evals, observation
completes before the resulting virtual filesystem state is published, so an
inspector limit cannot leave a file write behind.

## Metadata and coverage

Candidate selection follows word and nominal-type dependencies used by both
the actual test body and its expected expression. Temporary words referenced
by an expected expression are rejected as commit dependencies. Rename rewrites
expected-expression calls and reruns the affected test owner even when the
owner's word body does not call the renamed word. Expected-expression
execution uses an isolated trace; only the actual test body can satisfy
instruction or branch coverage.

Runtime coverage state now uses raw verified `SourceSiteId` values and
`(SourceSiteId, outcome)` pairs. Coverage obligations remain the verified IR
identities. Runtime requires each executed identity in the exact program or
detached-body source map; the exact synthetic Scope/local tags are charged for
fuel but excluded from authored coverage. Public uncovered labels are rendered
from that source map and sorted after comparison, so distinct obligations that
share a file/line/column retain distinct counts and duplicate display labels.
The same-span regression compiles four authored instructions at one source
location, executes only one branch, and verifies the unreached instruction and
false branch outcome remain uncovered. It also checks that a synthetic Scope
is absent from authored obligations while its instruction is still charged.
The focused fixture uses the public Compiler and interpreter APIs; a Flow
Runtime protocol entry is not yet available for an Engine-level collision case.

## Focused validation

The focused Release runs passed:

```powershell
dotnet run --project tests/AgentLang.Source.Tests -c Release  # 84 assertions
dotnet run --project tests/AgentLang.Acceptance -c Release    # 34 groups, 583 assertions
```

Initial attempts exposed tabbed expectation-marker handling and fixture
compilation/body setup issues. The marker parser and fixtures were corrected
before the passing runs above. The first raw-site run also exposed a missing
origin-map argument in the detached-body fixture; the fixture now supplies the
complete program mapping and a distinct marker for its synthetic Scope. Both
listed commands then passed, with no focused validation process active.

The acceptance additions cover typed nominal containers, literal and
runtime-error response compatibility, rejected type mismatches and effects,
expected-only word/type commit closure and reload, temporary-reference
rejection, rename of expectation-only callers, isolated branch coverage,
strict structured flags, virtual-file non-publication on inspection failure,
and a passing deep-value commit/reload whose optional DTO hits the inspection
depth bound.

## Integrated validation

The subsequent fresh full Release gate passed all 23 required checks, and the
pinned historical CLI comparison passed 314 selected checks. See
[report 021](021-integrated-flow-foundation.md) and its saved evidence. These
results validate the delivered slice; the full Flow migration and controlled
agent evaluation remain incomplete.
