# AgentLang

AgentLang is a small F# prototype for building a persistent, typed vocabulary through inspectable words. A host agent can discover available operations, stage definitions, evaluate and test them, then commit tested vocabulary for the next session.

AI agents are external coding tools that use the language. The language runtime contains no AI agents or model calls and works without an API key. Humans and ordinary scripts use the same commands. Model integration is an optional, separate experiment harness under `experiments/`; task sessions are dictionary transactions and logs.

This repository implements a prototype slice. The compiler checks and lowers source into verified typed semantic IR; the public runtime executes that IR through the interpreter, with no AST execution fallback. Effectful language primitives use virtual providers only; there is no host filesystem, network, or database access from language programs. An optional external experiment harness and a conventional business foundation are included. The [repeated external-agent comparison](reports/064-repeat-agent-purpose-review.md) observes tested vocabulary reuse; an overall efficiency advantage remains unproven. The Customer demo uses binary floating point and is not suitable for exact money. The Email validator below demonstrates a modest local policy and does not claim conformance with the full Internet email standard.

The default Flow authoring cutover passed the complete 27-check Release gate in
[report 052](reports/052-default-flow-authoring.md). Flow uses named typed inputs,
immutable locals, ordinary calls and static first-input dot chaining. Words
remain the unit of reusable vocabulary. Explicit Stack mode and metadata-based
historical loading remain supported; the runtime never tries another parser
after a source error. Both frontends execute the same verified semantic IR.
Close-to-first-use is advisory lint, and unrestricted mutable globals are excluded.

The decisions and scope live in [docs/PRD.md](docs/PRD.md) and [docs/DECISIONS.md](docs/DECISIONS.md).
The [current roadmap](docs/ROADMAP.md) distinguishes implemented features,
active agent validation and approved future work.

Flow/2 uses `fn`, plain record properties and `==`, with
omitted effects meaning `none`. See the [revised customer example](docs/EXAMPLE-SYNTAX-MIGRATION.md).
Select `syntaxVersion: 2` in JSON requests or `--syntax-version 2` in the human
CLI. Omitted version selectors retain Flow/1 for compatibility with existing
clients. Stored source always retains its declared frontend version.

Flow/2 also supports [closed nominal enums](docs/CLOSED-ENUMS.md) and exhaustive
matching in project functions. Finite input and return qualification for library
functions is implemented and validated locally;
native enum execution remains unavailable.

[Authoring through the runtime](docs/AUTHORING.md) explains inline documentation,
attached tests/examples, library coverage and revision-checked replacements.
Use JSONL help with the same syntaxVersion as your source—for example,
`{"op":"help","topic":"examples","syntaxVersion":2}`—or REPL :help TOPIC for
the corresponding inspectable contracts.

Flow/2 record-validation help includes executable predicate-owned rejection tests
and library qualification requests; see [report 121](reports/121-record-validation-guidance.md).
The [versioned-help trial](reports/123-versioned-record-help.md) passed all ten
independent cases and seven tests, with both functions library-qualified and no
structured errors. The [efficacy assessment](reports/124-efficacy-assessment.md)
finds demonstrated reuse and workable edits, but no established reliability
advantage over F#. The [matched repair comparison](reports/125-matched-pair-repair.md)
now records six fresh agents with 12/12 independent cases each. Both retained
Flow agents and both F# agents reuse their prior helper; one Flow agent omits
task finalization. Behavioral correctness and workflow completion are separate.
The [R09-inspired seed trial](reports/134-r09-discovery-study.md) adds an
agent-created checked customer payment total: 13/13 hidden cases, 163/163 attached
tests and completed task finalization. Its two functions are library-qualified;
prompt-delivery and property-style deviations are recorded. The completed
[six-agent shared-summary comparison](reports/135-r09-shared-summary-comparison.md)
has 13/13 public behavior scenarios in all six submissions, while only three
meet the frozen full-acceptance rubric. The other outcomes expose a finalization
failure, an undisclosed private helper-signature constraint, and a public
structural refactor failure. The study does not establish comparative reliability
superiority. After default-help improvements, a fresh
[guided composition trial](reports/137-guided-discovery-composition.md) passes
14/14 independent cases and 171/171 project tests, composing the earlier
agent-created helper without changing inherited definitions. This single
language-only trial supports feasibility, not a comparative reliability lead.
The [shared-rule maintenance study](reports/141-paid-invoice-maintenance.md)
now has frozen inputs, passing scorer controls and four fresh participants
dispatched. Results are pending. Its incorrect language control passes all 191
attached tests but fails independent acceptance, reinforcing the distinction
between test coverage and correct business expectations.

The implementation targets .NET 9 and uses no external test framework or model API key.

## Build and acceptance checks

From the repository root:

~~~powershell
dotnet build AgentLang.sln
dotnet run --project tests/AgentLang.Acceptance
~~~

## Optional native conformance

`AgentLang.Llvm` consumes the same verified semantic IR and emits a Windows x64
DLL for a bounded pure Int/Bool/Unit subset, including Int/Bool-backed nominal
scalars and their refinement validators. Building the solution compiles the
backend; executing native conformance additionally requires LLVM Clang,
`lld-link`, and an MSVC static runtime archive for stack-probe support:

~~~powershell
pwsh -NoProfile -File scripts/Verify-NativeConformance.ps1
~~~

The gate builds into an isolated `.agentlang/native-validation` directory and
executes generated code at both `-O0` and `-O2`. Override tool discovery with
`AGENTLANG_LLVM_CLANG`, `AGENTLANG_LLVM_LLD`, and
`AGENTLANG_COMPILER_RUNTIME_LIB` when needed. It is a local optional gate;
ordinary interpreter tests do not require LLVM. See
[the native architecture report](reports/103-llvm-architecture-and-native-slice.md)
and [typed native state re-entry](reports/107-native-state-reentry.md) for supported
semantics, evidence and limits. Native records use invocation scratch arenas and
independent retained outputs. A bounded standalone native mailbox controller is
validated in [report 126](reports/126-native-mailbox-dispatch.md). A general
release packager, JIT backend and real async I/O remain future work.

The owning-value mailbox integration is locally validated in
[report 131](reports/131-owning-native-mailboxes.md). Its dedicated local gate,
`pwsh -NoProfile -File scripts/Verify-OwningMailbox.ps1 -SerialBuild`, builds
fresh LLVM modules and exercises native initialize/begin/resume calls with
inline String payloads, retained byte banks and scratch reset. This is a
correctness gate; it does not measure service throughput or agent efficacy.

The focused [arena-policy comparison](reports/132-associated-arena-comparison.md)
also passes at O0/O2. Run `pwsh -NoProfile -File scripts/Verify-OwningMailboxPolicy.ps1`
to compare keeping actual scratch attached across suspension with returning it.
Keeping scratch avoids intermediate state copies but pins pool slots while
waiting. The bounded [real-I/O correctness comparison](reports/133-real-io-mailbox-correctness.md)
now passes both policies at O0/O2, including terminal cancellation and failed-resume
retry. Run `pwsh -NoProfile -File scripts/Verify-OwningMailboxRealIo.ps1` on Windows
with local loopback access. Throughput and whole-process memory comparisons remain pending.

A separate Windows C11 arena/mailbox experiment compares per-turn and
whole-request lifetimes. Run `pwsh -NoProfile -File scripts/Verify-NativeArenaMailbox.ps1`
for its fresh native safety checks and fixed comparison. See
[report 104](reports/104-native-arena-mailbox-feasibility.md) for measured memory
results and limits. This experiment is not yet integrated into language execution.

## Try Flow authoring

Start the JSON-lines CLI:

~~~powershell
dotnet run --project src/AgentLang.Cli -- --project .agentlang/flow-quickstart --jsonl
~~~

Send these requests, one per line:

~~~json
{"op":"define","syntaxVersion":2,"source":"fn increment(value: Int) -> Int {\n    doc \"Increase a value by one.\"\n\n    value.add(1)\n}\ntest increment/basic {\n    ::increment(41)\n    => 42\n}"}
{"op":"test","word":"increment"}
{"op":"commit","word":"increment"}
{"op":"eval","syntaxVersion":2,"code":"::increment(41)"}
{"op":"source","word":"increment"}
~~~

Evaluation produces 42; source inspection returns the authored Flow word.
An explicit `frontend: "stack"` on `define` or `eval` selects legacy RPN.
Malformed Flow never falls back to Stack. Other commands operate on the same
current dictionary regardless of the frontend that authored a word.

## Run the Customer demo

Start the human REPL:

~~~powershell
dotnet run --project src/AgentLang.Cli -- --project .agentlang --syntax-version 2
~~~

At its prompt, stage the Flow example, test it and commit reusable vocabulary:

~~~text
:define examples/customer.agent
:test-all
:commit customer.premium?
:commit customer.discounted-balance --library
:quit
~~~

A word commit requires passing attached tests. Library commits additionally
require every own-body executable instruction and supported branch outcome,
finite values for each direct Bool/enum parameter and supported finite return
domain, and an actual invocation of that function revision in its own passing
tests. See the [finite coverage contract](docs/FINITE-COVERAGE.md). Selected
dependencies and types commit with their callers.
Start a fresh process to reuse the vocabulary:

~~~powershell
dotnet run --project src/AgentLang.Cli -- --project .agentlang --syntax-version 2 --eval 'customer::discounted-balance(customer::new(kind = "premium", balance = 100.0))'
~~~

The result is 90. This example uses binary floating point and does not model
exact financial amounts. The REPL buffers incomplete Flow documents/expressions;
use `:define FILE` for several declarations in one atomic source document.
Use `--frontend stack` for the human REPL or one-shot legacy evaluation, with
preserved examples under [examples/legacy](examples/legacy). JSON-lines and
`--request` modes use per-request selectors instead of the process frontend flag.

## Language syntax

Ordinary calls and dot chains have the same statically resolved behavior:

~~~text
add(10, 20)
10.add(20)
~~~

Dot chaining passes its receiver as the first input, evaluates it once and
preserves written effect order. It is not object dispatch. `::name` addresses an
exact root dictionary name; `customer::premium?` addresses `customer.premium?`.
Declaration headers and test/example owners spell the dictionary identity
verbatim, with dots between namespace segments when present.
Short calls reject ambiguity rather than choose a changing meaning.
Plain record fields use `customer.kind`; a function call still requires
parentheses, such as `customer.premium?()`. Property access cannot invoke an
arbitrary dictionary function. `==` requires identical operand types, including
nominal identity; it does not unwrap Email to String or convert numeric types.

~~~text
fn positive-part(value: Int) -> Int {
    doc "Keep a positive value, otherwise return zero."

    if int::greater-than(value, 0) { value } else { 0 }
}
test positive-part/positive {
    ::positive-part(3)
    => 3
}
test positive-part/nonpositive {
    ::positive-part(-1)
    => 0
}
~~~

Every function declares its types. Omitted effects mean pure; effectful functions
must explicitly declare their effects. Named inputs and `let name = expression`
locals are immutable and word-scoped. Locals near first use are advisory lint.
Both conditional paths must return the same types; no value is silently dropped.
Tests assert literal values, structured runtime errors or independently evaluated
pure expected expressions. Examples provide metadata and do not count as tests.

A Flow document can declare records, refined scalar types, words, tests and
examples. The whole proposed dictionary validates before any member is staged:

~~~text
record Customer { field kind: String; field balance: Float; }
type Email : String { validate email::valid?; }
type MetersPerSecond : Float {}
~~~

A validator must accept its scalar base and return Bool, with no direct or
transitive effects. [The refined-type example](examples/refined-types.agent)
defines a modest Email policy, not full Internet email conformance. Construction
checks that policy; unwrapping is explicit:

~~~text
Email::value(Email::new("dev@example.com"))
MetersPerSecond::new(3.0)
~~~

Email is distinct from String, and MetersPerSecond is distinct from Float and
KilometersPerHour. No representation-based coercion is allowed. Existing types
and frozen validator semantics cannot be silently replaced. Flow type sources
use manifest v3; historical v1/v2 source objects remain readable without rewriting.

## Interactive updates and inspection

Use `:source WORD` for a word and `:source --type TYPE` for the exact type source.
Protocol equivalents are `{"op":"source","word":"increment"}` and
`{"op":"source","type":"Email"}`. Source hashes, stable identities, tests,
examples, history and dependency queries remain available after reload.
`{"op":"format","syntaxVersion":2,"source":"..."}` returns canonical Flow
source without changing the dictionary. The human equivalent is `:format FILE`.
Submit the returned text through the normal definition/revision operation to
accept it as retained source. Formatting preserves explicit versus omitted
effects and puts a blank line between metadata and executable code.

After defining a Flow word, a separate test/example declaration can add a new
case to that word. Existing case replacement requires explicit owner revision
CAS. A file-based revision uses:

~~~text
:define replacement.agent --replace --expected-revision 1
:test WORD
:replace-word WORD
~~~

New multitype/multiword documents are add-only. Temporary documents cannot
introduce project types. Case-only documents must name one existing Flow user
word; test generated constructors/accessors through a user word, or select
Stack explicitly for historical direct generated-owner cases.

## Tests, temporary words and discovery

Project words require at least one passing attached test. Library words add
complete actual own-body instruction and supported control-flow coverage,
per-parameter finite input coverage, and finite return coverage. The target
revision must actually run in its own passing tests; expected expressions and
failed tests supply no qualifying evidence. Unsupported finite domains fail
qualification instead of being treated as covered. These checks show execution
and finite alternatives, while meaningful assertions and boundary cases still
establish behavior. See the [finite coverage contract](docs/FINITE-COVERAGE.md)
and [testing policy](docs/TESTING.md). Downstream project words can focus on
integration rather than repeat all library qualification.

Temporary describes dictionary lifetime; library describes quality. A temporary
word can be tried and discarded, or promoted for tested persistence. A local
binding is a value inside an invocation, not a dictionary word. Neither term
specifies arena allocation or memory lifetime.

Closed List, Option and Result types preserve nominal payload types even when
empty or unsuccessful. Static callbacks and exhaustive cases share the same
verified semantics across frontends. [The container example](examples/containers.agent)
uses typed construction, cases, map/filter/each and branch tests.

The protocol supports words, describe, search, source, dependencies, callers,
effects, ir, tests/examples, commit/promotion, task transactions/logs, history,
diff and current stack inspection. Structural queries include type-of,
search-type/output/effect/dependency, transitive graphs and bounded compact
context. Context bytes are measured payload bytes, not model tokens.
See [discovery contracts](docs/DISCOVERY.md) and `:help` for CLI controls.

Use `{"op":"words","compact":true}` or `:words --compact` for a names-only
inventory, then `describe` for the selected word's full metadata. Descriptions
include a parser-verified `flowReference` for exact calls in Flow source.

## Effect permissions

Language programs can invoke only trusted primitives. A primitive's declared effect does not grant permission to perform it. The host denies effects by default; pass explicit capabilities to enable them:

~~~powershell
dotnet run --project src/AgentLang.Cli -- --allow fs.read,fs.write,console.write --jsonl
~~~

The initial file and clock providers are deterministic and virtual. The --clock flag sets the value returned by clock.now.

## External experiment harness

The optional harness consumes the public runtime interface. Its offline smoke run needs no model credential:

~~~powershell
dotnet run --project experiments/AgentLang.Benchmarks -- run --task experiments/AgentLang.Benchmarks/fixtures/customer-discount-task.json --provider scripted --script experiments/AgentLang.Benchmarks/fixtures/customer-discount-script.json --frontend stack --seed-source examples/legacy/customer.agent
~~~

This explicitly replays the historical Stack scripted fixture and checks protocol orchestration. Normal harness authoring selects Flow; the configured frontend also selects the seed/parser and compact language primer. This is an infrastructure check, not evidence that an AI agent implemented a task. [docs/HARNESS.md](docs/HARNESS.md) documents live-provider configuration, retention modes, request limits, saved traces, and independent task oracles. [docs/BUSINESS.md](docs/BUSINESS.md) describes the conventional business foundation and its exact-money contract.

## Current limits

The runtime interprets verified typed semantic IR; source ASTs are used for authoring and diagnostics. Records, nominal scalar types, and closed containers are supported. Generic user definitions and local file, database, network, or process access are not implemented. The file and console effects use in-memory providers, and the clock is fixed by the host.

The typed semantic IR model, verifier and source lowerer are available, with closed executable types/effects and linked call identities. Runtime execution now routes through the IR interpreter; [the migration plan](docs/IR-MIGRATION.md) records the cutover and its acceptance/parity evidence. Bounded LLVM AOT is implemented; development JIT and general release support remain follow-ons. [Vocabulary analysis](docs/VOCABULARY-ANALYSIS.md) provides exact structural duplicate candidates and static call expansion; runtime warnings and reuse metrics remain pending.

Committed projects use hashed manifests and source objects under `.agentlang/store`; `dictionary.agent` remains the readable export and legacy import format. Stable word IDs and prior revision sources survive reload. `history` and `diff` inspect those durable revisions. `rename` rewrites semantic calls and attached cases while preserving identity; `deprecate` retains callable behavior; `replace-word` commits a staged replacement after its own and affected caller tests pass. Named `snapshot.save`/`snapshot.load` operations restore committed vocabulary and virtual provider state while retaining current host capabilities. See [storage](docs/STORAGE.md), [canonical source](docs/SOURCE.md), and [expected-error tests](docs/TEST-ERRORS.md).

The [business-language fixture](reports/075-business-language-transitions.md) now includes strict values, immutable Store operations, subscription/invoice/payment/email transitions and a reproducible seed. The [exact-Money agent study](reports/076-exact-money-agent-policy-study.md) accepted seven of nine fresh three-mode trials and observed classification reuse; two Flat trials failed documentation metadata. An efficiency advantage remains unproven. Provider outcomes are pure input data; broader matched business-task adapters and controlled benchmark results remain pending. The separate `replace OLD NEW` operation is also pending. Conventional read/search/edit/validation tools have [a tested foundation](docs/CONVENTIONAL.md); executing conventional F# code uses host permissions. Task logs report runtime vocabulary, test, error and simulated effect counts. Model measurements belong to the separate [experiment harness](docs/HARNESS.md).

The [requirements ledger](docs/REQUIREMENTS.md) tracks the full PRD beyond this slice. [Milestone reports](reports/README.md) distinguish validation evidence from research results. Run `./scripts/Validate.ps1` for the same fresh Release build and checks used by CI.

Use `--project .agentlang` to keep the prototype dictionary and task logs in the ignored project-local directory instead of the current working directory.

## Native mailbox ownership demonstration

Run `pwsh -NoProfile -File scripts/Verify-NativeMailboxSuspension.ps1` for the
standalone source-backed mailbox experiment. It builds fresh isolated artifacts,
runs Core/O0/O2 handlers, and checks retained-state retry and scratch reuse against
an independent oracle. See [report 108](reports/108-native-mailbox-suspension.md)
and [the Flow/2 handlers](experiments/AgentLang.NativeMailbox/mailbox.flow).
This uses a .NET experiment host; it is not a server throughput benchmark or a
compiler-free native mailbox runtime.

## Standalone native mailbox dispatch

Run `pwsh -NoProfile -File scripts/Verify-NativeDispatch.ps1` for a fresh local
Windows x64 build of compiled Flow/2 handlers and the C mailbox controller.
The compiler/bootstrap uses .NET; the turn-execution process does not. The
verifier passes 353 checks across O0/O2 and repeated module builds, including
state preservation, token rejection, ABI layouts and native dependency audits.
See [report 126](reports/126-native-mailbox-dispatch.md) for exact storage totals,
validation and limits. This is not a real-I/O throughput comparison. The next
[arena comparison](docs/ASYNC-ARENA-EVALUATION.md) explicitly tests keeping the
mailbox's stack associated during async waits versus returning it to a pool.
The [optional native reset profile](reports/138-native-reset-profile.md) removes
full-capacity scratch clears at checkout/release while retaining the existing
checks. The subsequent [trusted-generated profile](reports/139-trusted-generated-native-profile.md)
also removes diagnostic bitmap storage and live-prefix poisoning. Both policies
pass the same socket fixtures at O0/O2 across all three profiles. Compile mailbox
modules with `--runtime-profile trusted-generated` and the native host with
`AL_OWNING_TRUSTED_GENERATED=1` to select matching trusted builds. Diagnostic
remains the default. The first [matched native/F# load comparison](reports/140-matched-mailbox-load.md)
passes 197 correctness-checked trials and an independent data audit. At a matched
delayed-I/O point, native RETURN uses about 5.2 MiB peak commit versus F#'s
26.2 MiB; KEEP with sixteen arenas recovers the throughput lost by a four-arena
pool at about 8.1 MiB. Native RETURN's high short-run rate fails its 30-second
confirmation. These bounded host results do not establish general server
throughput or improved agent edit reliability.
