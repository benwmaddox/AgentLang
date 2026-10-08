# 119 — Whole-record construction invariants

This implementation checkpoint adds an optional pure validator to nominal
records and carries its resolved target through typed IR construction. Focused
Core, Runtime, Storage, business-extension and native conformance checks pass.
The aggregate local Debug run passed 36/37 checks; the remaining inspection
script needed the new IR union-case arity. After that one-line repair, its exact
command passed all 31 assertions. The aggregate was not rerun. This report is
implementation evidence, not a fresh-agent or feature-adoption result.

## Delivered mechanism

A record can declare one validator with the same nominal record as its exact
input and Bool as its result. The compiler resolves and freezes that target in
the record definition and every generated `MakeRecord` operation. Verification
rejects wrong signatures, effects, tampered constructor metadata and direct or
indirect cycles in which a validator constructs its own type. The formatter
includes the binding in debug IR format version 4.

Construction creates the complete record candidate, invokes the predicate,
and exposes the record only when the predicate returns true. A normal false
result raises `RECORD_VALIDATION_FAILED` at the construction site; predicate
execution errors propagate. Records without validators retain their previous
construction behavior. Existing serialized storage remains version 3, and the
native ABI is unchanged.

The candidate is unchecked while its validator runs. The predicate and its
pure helpers may inspect it, but must not assume the invariant already holds.
Purity and the Bool-only result prevent returning the candidate or escaping it
through effects or callbacks. The compiler does not treat the predicate as a
proof and makes no optimization assumptions from it. The C record allocator
remains trusted internal machinery; arbitrary C callers do not receive the
language-level validator guarantee.

The interpreter and supported LLVM record constructors enforce the same
predicate. Native O0/O2 conformance covers valid and rejected values, nested
pure helpers, diagnostic parity and retained-root reconstruction. Native List
support is not added.

## Coverage and boundaries

An arbitrary predicate can rule out field combinations, so finite coverage
does not enumerate a refined record as though every raw field combination were
valid. A validated record with finite projections is explicitly unsupported
unless a complete valid-value domain can be proven. This includes a validated
zero-field record: its unrefined domain is a closed singleton, not an open
shape. Fully open records with no finite obligations can remain observable
without enumeration. No predicate enumeration or proof mechanism is added.

This conservative rule also limits qualification usability: an authored
function returning a validated record with Option, Bool or enum finite
projections cannot currently qualify, even if its validator is otherwise
library-ready. The predicate itself can qualify by observing both true and
false: a rejected-constructor test observes the validator's completed false
return before the constructor raises its expected error. This return must be
retained by coverage. If the target itself throws before returning, it
contributes no return observation; the observer must not fabricate one.
Focused Runtime and Business.Transitions tests confirm this observation
boundary; the finite-domain policy is not relaxed here. The business extension
commits `lookup.valid?` itself at library maturity after tests observe both
branches and both Bool returns, including the completed `false` result before
the expected constructor error. This does not qualify a function that returns
the refined lookup record.

Recorded Flow Runtime development attempts exposed test-harness expectation
mismatches: structural coverage can reject before finite coverage, finite
return gaps are reported by describe rather than every commit diagnostic,
and structured diagnostics need not contain a nonempty expected list. The
throw-before-return control passed after its assertion queried the proper
finite observation field; there was no evidence of a fabricated return.
Raw failed attempts are retained alongside final results.

Focused Runtime and Storage tests pass durable create/commit/reload checks. The
Flow/2 business extension also passes its focused run: a library-qualified
predicate and validated record survive reload with the same stable validator
target. Provider-state assertions and broader module/library-closure and
test-local override rules remain pending. No fresh-agent behavior or adoption
result is inferred from implementation tests.

## Validation recorded

The Core Debug build passed with 0 warnings and 0 errors. The focused Core
families passed with these assertion counts: IR 191, interpreter 93, debug
formatting 44, Flow 1,110 and Source 101. After the final zero-field finite
coverage guard correction and validator-only snapshot invalidation test, the
Core Debug build and IR runner were rerun; both passed, with the IR runner at
191 assertions. The other focused Core runners were not repeated after this
Core-only correction. The commands were:

```powershell
dotnet build src/AgentLang.Core/AgentLang.Core.fsproj -c Debug
dotnet run -c Debug --project tests/AgentLang.IR.Tests/AgentLang.IR.Tests.fsproj
dotnet run -c Debug --project tests/AgentLang.IR.Interpreter.Tests/AgentLang.IR.Interpreter.Tests.fsproj
dotnet run -c Debug --project tests/AgentLang.IR.Formatting.Tests/AgentLang.IR.Formatting.Tests.fsproj
dotnet run -c Debug --project tests/AgentLang.Flow.Tests/AgentLang.Flow.Tests.fsproj
dotnet run -c Debug --project tests/AgentLang.Source.Tests/AgentLang.Source.Tests.fsproj
```

The fresh native Debug build passed with 0 warnings and 0 errors, and the LLVM
conformance runner passed 455 assertions, including O0/O2 record-validator
cases. Source inventories were unchanged during that build and run. The exact
logs and before/after hashes are in
`.agentlang/record-validator-001/build-02/`. The final zero-field correction
changed only Core finite-coverage classification and its IR test; the final
Core/IR rerun is recorded above. The complete aggregate run and its isolated
inspection-script repair are recorded separately below.

The additional focused Runtime runner passed 30 groups / 979 assertions, and
Storage passed 16 groups / 370 assertions. The Business.Transitions runner
passed 9 groups / 5,479 assertions while preserving its 53-word, 31-type,
154-test and 44-example baseline. Its separate validated-lookup extension adds
1 function, 1 record, 3 tests and 2 examples; `lookup.valid?` commits as a
library function, and its validator target and tests survive a fresh Engine
reload. The prior populated-delivery extension remains 2 functions, 1 type,
7 tests and 3 examples.

The first aggregate Debug solution build failed with two `FS0764` errors:
Vocabulary and Discovery record fixtures omitted `Validator = None`. Both were
repaired, and the solution rebuild passed with no warnings or errors. The
failed attempt and repair log remain in the evidence archive.

The subsequent command was:

```powershell
./scripts/Validate.ps1 -Configuration Debug -ReportPath .agentlang/record-validator-001/full-validation.json
```

It completed in 21 minutes 29 seconds with **36/37 checks passing**. The sole
failure was compilation of `scripts/Verify-FlowCallBindings.fsx`, whose pattern
still expected two `MakeRecord` fields. Its pattern now ignores the third,
implicit validator field while preserving authored-call inspection. The exact
failed command was rerun successfully:

```powershell
dotnet fsi --exec --reference:src/AgentLang.Core/bin/Debug/net9.0/AgentLang.Core.dll scripts/Verify-FlowCallBindings.fsx
```

That probe passed 31 assertions. Its script and Core binary hashes remained
stable during the run. No product source changed after the aggregate run, and
all 37 required checks have passing evidence across these two runs. This is
not a claim that the aggregate command was rerun or exited successfully.
Business-policy preflight passed 98 checks across 30 independent outcomes;
it accounted for 15 minutes 41 seconds of the aggregate duration. CI remains
manual-only.

[Evidence index](evidence/119-record-construction-invariants/index.json) lists
source hashes, focused/native results, raw failed attempts, independent review,
the full aggregate log and the repaired probe. Auxiliary process reports are
compressed separately; runtime binaries are excluded. The frozen report-108
Release runtime is unchanged.

Next is one bounded fresh-agent construction-invariant adoption trial, using
runtime help and independent accepted/rejected cases. Comparative reliability,
vocabulary retention, provider-state assertions and production memory behavior
remain separate questions.
