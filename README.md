# AgentLang

AgentLang is a small F# prototype for building a persistent, typed vocabulary through inspectable words. A host agent can discover available operations, stage definitions, evaluate and test them, then commit tested vocabulary for the next session.

AI agents are external coding tools that use the language. The language runtime contains no AI agents or model calls and works without an API key. Humans and ordinary scripts use the same commands. Model integration is an optional, separate experiment harness under `experiments/`; task sessions are dictionary transactions and logs.

This repository implements a prototype slice. The compiler checks definitions into a checked expression tree, and the runtime interprets that tree directly. Effectful language primitives use virtual providers only; there is no host filesystem, network, or database access from language programs. An optional external experiment harness and a conventional business foundation are included; no measured agent comparison is claimed. The Customer demo uses binary floating point and is not suitable for exact money. The Email validator below demonstrates a modest local policy and does not claim conformance with the full Internet email standard.

The decisions and scope live in [docs/PRD.md](docs/PRD.md) and [docs/DECISIONS.md](docs/DECISIONS.md).

The implementation targets .NET 9 and uses no external test framework or model API key.

## Build and acceptance checks

From the repository root:

~~~powershell
dotnet build AgentLang.sln
dotnet run --project tests/AgentLang.Acceptance
~~~

## Run the Customer demo

Start the human REPL in a project directory:

~~~powershell
dotnet run --project src/AgentLang.Cli -- --project .agentlang
~~~

At the agentlang> prompt, stage the example, run its tests, and commit the project vocabulary:

~~~text
:define examples/customer.agent
:test-all
:commit customer.premium?
:commit customer.discounted-balance
:quit
~~~

Each word commit requires attached passing tests, and dependencies are committed before their callers. The record definition commits with the first word. Start a fresh process to reuse the committed vocabulary:

~~~powershell
dotnet run --project src/AgentLang.Cli -- --project .agentlang --eval '"premium" 100.0 customer.new customer.discounted-balance'
~~~

The result is 90. This example uses binary floating point to keep the prototype small; it does not model exact financial amounts.

The REPL accepts single-line expressions and buffers multiline declarations, if blocks, and Option/Result matches. Use :define FILE to load a file containing several declarations. The :help command lists available controls.

## Language syntax

Expressions are concatenative. Literals and word calls run left to right, with signatures describing stack inputs and outputs from bottom to top:

~~~text
10 20 add
~~~

This leaves 30. Strings are JSON-style quoted literals. Available scalar literals are Int, Float (include a decimal point), Bool, String, and unit.

A declaration file can contain records, words, tests, and examples. Record field order defines constructor argument order. Record constructors and accessors use the record name with its first letter lowercased; a Customer with kind String and balance Float generates:

~~~text
customer.new : String Float -> Customer
customer.kind : Customer -> String
customer.balance : Customer -> Float
~~~

A named word declares its complete stack signature, effect set, optional documentation, and body:

~~~text
word add-one : Int -> Int
    effects none
    doc "Add one to an integer."
    1 add
end

test add-one/basic
    41 add-one
    => 42
end
~~~

Every word must explicitly say effects none or list effects such as fs.read or fs.write. Tests are attached by naming the word in the test header. An example block has the same body-and-expected-value form, but remains descriptive metadata and is not run as a test.

The form let name consumes the top stack value and saves it as a word-local binding; $name pushes it again. An if consumes the top Bool; both branches must leave the same stack types. An omitted else is an empty branch:

~~~text
word positive-part : Int -> Int
    effects none
    let input
    $input 0 int.greater-than
    if
        $input
    else
        0
    end
end
~~~

Nominal wrappers keep semantic values distinct from their underlying representation. Scalar constructors use the form Type.new, and explicit unwrapping uses Type.value. For example, [examples/refined-types.agent](examples/refined-types.agent) defines Email as String and distinct speed units over Float:

~~~text
"dev@example.com" Email.new Email.value
3.0 MetersPerSecond.new
3.0 MetersPerSecond.new 4.0 KilometersPerHour.new equals
~~~

The first expression constructs and explicitly unwraps an Email. The second constructs a speed value. The final expression fails static type checking because the nominal speed units are distinct. The validator requires an at-sign, a dot, no spaces, and no leading or trailing at-sign. It remains an illustrative acceptance rule, not a complete email validator.

A validator must accept exactly the base type and return one Bool; it must be pure. MetersPerSecond does not convert to a plain Float or another unit implicitly.

## JSON-lines protocol

Run the same dispatcher without prompts or extra output:

~~~powershell
dotnet run --project src/AgentLang.Cli -- --project .agentlang --jsonl
~~~

Each input line is one JSON object with an op field. Operation arguments can be top-level fields or grouped under args. Each request produces exactly one compact JSON response line. Responses contain ok, kind, and text; successful operations may include data, while failures include a structured error with a stable code and message.

Example requests:

~~~json
{"op":"eval","code":"10 20 add"}
{"op":"define","source":"word add-one : Int -> Int\neffects none\n1 add\nend\ntest add-one/basic\n41 add-one\n=> 42\nend"}
{"op":"test-all"}
{"op":"commit","word":"add-one"}
{"op":"describe","word":"add-one"}
~~~

The dispatcher supports evaluation and definition, vocabulary discovery (words, describe, search, source, dependencies, callers, effects, ir), examples and tests, commits and promotion, task begin/status/commit/abort/log, history, diff, and the current stack view. Commit and commit-word accept a word and a library flag. A library commit requires passing tests plus complete executable-instruction coverage and both outcomes of every conditional. Use the Human REPL sequence below to see the gate and coverage report:

~~~text
:define examples/customer.agent
:test-all
:commit customer.premium?
:commit customer.discounted-balance --library
:describe customer.discounted-balance
~~~

A failed gate returns LIBRARY_COVERAGE_INCOMPLETE with uncovered instruction and outcome locations. Library tests must cover both conditional branches, both Option/Result cases, empty and nonempty iteration, and kept/dropped filter outcomes where used. A successful description reports coverage and the selected maturity. Library maturity survives reload and later replacements. See [testing policy](docs/TESTING.md).

Temporary describes a word's lifetime, while library describes its quality requirements. A temporary word can be tried within a session and discarded; promoting it makes it a candidate for persistence. A project word requires attached passing tests to commit. A library word additionally requires complete instruction and supported control-flow coverage. A local binding created by `let` is a value within one invocation, not a dictionary word.

Closed `List<T>`, `Option<T>`, and `Result<T,E>` values preserve their declared types even when empty or unsuccessful. Static callbacks and exhaustive case blocks are described in [docs/CONTAINERS.md](docs/CONTAINERS.md), with runnable examples in [examples/containers.agent](examples/containers.agent).

## Effect permissions

Language programs can invoke only trusted primitives. A primitive's declared effect does not grant permission to perform it. The host denies effects by default; pass explicit capabilities to enable them:

~~~powershell
dotnet run --project src/AgentLang.Cli -- --allow fs.read,fs.write,console.write --jsonl
~~~

The initial file and clock providers are deterministic and virtual. The --clock flag sets the value returned by clock.now.

## External experiment harness

The optional harness consumes the public runtime interface. Its offline smoke run needs no model credential:

~~~powershell
dotnet run --project experiments/AgentLang.Benchmarks -- run --task experiments/AgentLang.Benchmarks/fixtures/customer-discount-task.json --provider scripted --script experiments/AgentLang.Benchmarks/fixtures/customer-discount-script.json --seed-source examples/customer.agent
~~~

This checks protocol orchestration and existing customer vocabulary. It is a scripted infrastructure check, not evidence that an AI agent implemented a task. [docs/HARNESS.md](docs/HARNESS.md) documents live-provider configuration, retention modes, request limits, saved traces, and independent task oracles. [docs/BUSINESS.md](docs/BUSINESS.md) describes the conventional business foundation and its exact-money contract.

## Current limits

The runtime interprets a checked expression tree; it does not emit bytecode. Records, nominal scalar types, and closed containers are supported. Generic user definitions and local file, database, network, or process access are not implemented. The file and console effects use in-memory providers, and the clock is fixed by the host.

Committed projects use hashed manifests and source objects under `.agentlang/store`; `dictionary.agent` remains the readable export and legacy import format. Stable word IDs and prior revision sources survive reload. `history` and `diff` inspect those durable revisions. `rename` rewrites semantic calls and attached cases while preserving identity; `deprecate` retains callable behavior; `replace-word` commits a staged replacement after its own and affected caller tests pass. Named `snapshot.save`/`snapshot.load` operations restore committed vocabulary and virtual provider state while retaining current host capabilities. See [storage](docs/STORAGE.md), [canonical source](docs/SOURCE.md), and [expected-error tests](docs/TEST-ERRORS.md).

The separate `replace OLD NEW` operation, full business-language fixture and controlled benchmark results remain pending. Conventional read/search/edit/validation tools now have [a tested foundation](docs/CONVENTIONAL.md); executing conventional F# code uses host permissions. Task logs report runtime vocabulary, test, error and simulated effect counts. Model measurements belong to the separate [experiment harness](docs/HARNESS.md).

The [requirements ledger](docs/REQUIREMENTS.md) tracks the full PRD beyond this slice. [Milestone reports](reports/README.md) distinguish validation evidence from research results. Run `./scripts/Validate.ps1` for the same fresh Release build and checks used by CI.

Use `--project .agentlang` to keep the prototype dictionary and task logs in the ignored project-local directory instead of the current working directory.
