# Flow/2 authoring implementation

Status: implemented and locally validated, 2026-10-07.

The quick four-condition comparison is complete (report 099). This milestone
implements the approved authoring revision over the existing typed semantic IR:
`fn`, plain-record properties, typed `==`, default pure effects and explicit
formatting with a metadata/body blank line.

Flow/2 is selected explicitly with JSON `syntaxVersion: 2` or human CLI
`--syntax-version 2`. An omitted selector preserves Flow/1 compatibility.
Stack supports only version 1. Parsing, retained source, test/example attachment,
reload, rewrite and formatting must use the selected or stored source version;
no parser fallback or automatic migration is allowed.

The formatter returns text without writing files or dictionary state. A normal
definition/revision operation accepts the formatted bytes, regenerating source
spans and call bindings. Properties and equality are distinct authoring nodes,
then lower through existing typed operations. Property syntax cannot invoke an
arbitrary function; equality preserves nominal identity and type checks.

The three teaching examples (customer, refined-types, containers), CLI and
authoring documentation move together. Business fixtures and historical study
sources remain version-1 compatibility inputs. Module boundaries, library-call
closure, finite input/output qualification and injectable effects remain the
next separate milestone; this syntax change does not claim those requirements
are implemented.

Validation must cover legacy source, field/method disambiguation, incompatible
and nominal equality, omitted/transitive effects, formatter idempotence,
definition/test/library commit/reload, rewrites and malformed version selectors.
Validated focused Flow, Runtime, Source, Storage and CLI suites, plus applicable full
local acceptance from fresh artifacts. The frozen Release CLI hash remains
`bbfe7d079f568a431148298de86a3aa26304a160781c2756591be070735f274a`.

The pre-change Core baseline built with zero warnings/errors using isolated
`.agentlang/flow2-baseline-build` outputs. Integrated results will be recorded
after implementation; a baseline build is not evidence of the new behavior.

## Validation evidence

Fresh isolated Release solution build: zero warnings and errors. All outputs
use `.agentlang/flow2-validation`; the frozen experiment CLI and dependencies
remain byte-for-byte unchanged. The focused final suites passed: Flow 1,047
assertions, Flow Runtime 25 groups/822 assertions, and the host-constructed
structural binding probe 31 assertions. The probe fixture now supplies explicit
source-version/effects metadata required by the new host contract.

Ten process regression gates passed: persistence projection, matched renewal
fixtures (Stack and Flow), snapshots, Flow renewal acceptance and replay, early
negative controls, trial hosts v1/v2 and parser limits. Selected cross-binary
legacy behavior agrees with the frozen runtime; that parity report explicitly
makes no independent claim about the binary's internal execution backend.

The existing `Validate.ps1` was not invoked unchanged because its default build
would overwrite pinned experiment artifacts. The same 22 local acceptance
executables run directly from fresh isolated outputs, with `AGENTLANG_TEST_CLI`
selecting the candidate for CLI integration tests. The business-policy preflight
which hard-codes shared outputs is not part of this milestone's gate. No CI run
was requested or triggered.

Earlier integrated failures are retained: property accessor IR reconciliation,
authored equality operand paths, an outdated unsupported-version fixture and a
property-receiver assertion. The final implementation maps property access to
its accessor-call node; only calls nested in a receiver use `PropertyReceiver`.
The legacy default-version harness now uses an exact Flow/1 teaching fixture
instead of the migrated Flow/2 example. Historical experiments are unchanged.

Raw build, suite and process evidence is archived under
[evidence/100-flow2](evidence/100-flow2/), with a SHA-256 index. These are dirty
checkout validation results, not a claim of an exact committed-source CI run.
This authoring milestone does not implement scoped overrides, LLVM or arenas.