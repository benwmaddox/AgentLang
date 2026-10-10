# Authoring through the runtime

The older examples below describe Flow/1, which remains the default when a
request omits `syntaxVersion`. Select `syntaxVersion: 2` in JSON requests or
`--syntax-version 2` in the human CLI for `fn`, plain record properties, typed
`==` and omitted pure effects. See the [Flow/2 examples](EXAMPLE-SYNTAX-MIGRATION.md).
The dictionary and semantic IR are shared; each retained definition preserves
its source version. Select the same version for attached tests and replacements.
Runtime `help` requests also accept `syntaxVersion: 2`.

Flow/2 distinguishes `record.field` from `.function(...)`: a property must be a
declared field of a plain record, and function calls require parentheses.
Equality does not perform nominal or numeric coercions. `==` binds more loosely
than calls/properties, and chained comparisons require explicit parentheses.
Effects and documentation form the metadata block, followed by a blank line.
`format` returns canonical source without committing a change; accept formatted
source through the ordinary definition and revision checks.

Start discovery before editing. Use a names-only inventory, search for a focused
term, then inspect a known word with a bounded context:

```json
{"op":"words","compact":true}
{"op":"search","query":"add"}
{"op":"context","word":"add","maxDepth":2,"maxWords":6,"maxUtf8Bytes":4096}
```

`search` matches names, documentation and signatures; use `search-type`,
`search-output`, `search-effect` or `search-dependency` for structural queries.
`context` includes reachable word and type summaries within its limits; use
`transitive-dependencies` to inspect the full dependency closure. Use `describe`
to inspect signatures and the exact `flowReference` call spelling before
composing source. Names in protocol queries use dots; nominal types stay
distinct: a Money value is not an Int, and Email is not String.

Ask the runtime for authoring instructions through the same JSONL session:

```json
{"op":"help"}
{"op":"help","topic":"define"}
{"op":"help","topic":"replacement"}
{"op":"help","topic":"examples"}
```

The human REPL uses `:help TOPIC`. Plain `:help` still shows CLI usage.
The external model harness exposes runtime help through its existing inspect
tool; it does not add another tool or an AI component to the language.
An experiment host must allow the help operation explicitly.
The existing version-1 subagent inspection classifier does not count `help`
toward its optional inspection-response budget. Use unbudgeted follow-up trials
until a separately versioned host classifies it; do not claim that classifier
provides a complete retrieval budget for sessions with help enabled. The model
harness's complete per-request context-byte accounting is a separate mechanism.

## Documentation, tests and examples

Documentation belongs inside the word. Comments do not populate documentation
metadata, and `documentation` is not a Flow define request field.

```text
word tutorial.sign(value: Int) -> Int {
    effects none
    doc "Returns -1 for negative integers and 1 for zero or positive integers."
    if int::less-than(value, 0) { -1 } else { 1 }
}
test tutorial.sign/negative { tutorial::sign(-2) => -1 }
test tutorial.sign/zero { tutorial::sign(0) => 1 }
test tutorial.sign/positive { tutorial::sign(2) => 1 }
example tutorial.sign/negative { tutorial::sign(-2) => -1 }
```

Send this complete source in `define.source`, run its tests, inspect metadata,
then commit. Each test/example owner uses the dictionary name before the slash.
A bare `=>` expectation must be a supported literal. Tests also support
`=> value expression` and `=> error CODE`; examples accept literals only.
To assert a nominal Money result with a literal, unwrap it with
`Money::value(...)` and compare an Int literal. A nominal constructor expectation
must use the explicit `=> value` form, not a bare constructor after `=>`.
Expectation-side calls do not contribute coverage of the tested word.
Flow/2 tests can add [effect-count assertions](EFFECT-ASSERTIONS.md) to check
target-scoped provider calls alongside returned values.

External `tests` and `examples` arrays apply to a single-word define or
replacement request. Multi-declaration documents require inline cases, as
above. Attachment-only source must identify one owner; replacing or removing
existing attachments requires the replacement revision checks.

```json
{"op":"task.begin","goal":"Add an inspectable sign operation"}
{"op":"define","source":"<complete Flow source above>"}
{"op":"test","word":"tutorial.sign"}
{"op":"describe","word":"tutorial.sign"}
{"op":"example","word":"tutorial.sign"}
{"op":"commit","word":"tutorial.sign","library":true}
{"op":"task.commit"}
```

Before replacing a function or its tests, inspect the current test bodies:

```json
{"op":"tests","word":"tutorial.sign"}
{"op":"tests","word":"tutorial.sign","includeSource":true}
{"op":"tests","word":"tutorial.sign","includeSource":true,"caseName":"negative"}
```

The default is a sorted array of case names. `includeSource: true` returns
records with `word`, `name`, `source`, `sourceHash`, `frontend` and `syntaxVersion`.
`caseName` selects one existing case in either view. The source view reads current
attachments, including staged and temporary cases; it does not need durable
history. It does not execute tests, call effect providers or update coverage.

For a case inside a `test-file`, the record also includes `testFile` with its
scope, full source, source hash and dependency replacements. The case's `source`
is only its own authored test. Read the enclosing source to understand which I/O
functions are replaced and to preserve that setup when editing. The two hashes
identify the exact UTF-8 text in their respective source fields; a nested case
hash is not the enclosing wrapper's storage reference. Real execution and the
isolated test provider remain separate, as described in
[test dependency replacements](TEST-FILE-DEPENDENCIES.md).

Library persistence requires passing attached own tests, complete supported
own instruction/branch coverage, finite input and return evidence, and at least
one actual call to that exact function revision from its own passing tests.
Each direct Bool or enum input position is tracked independently. Return
positions require every value in a proven finite domain, including inhabitable
Option/Result cases and combinations inside a finite record. For open composites,
the gate checks applicable finite fields and tags without multiplying unrelated
positions or projections together. Int, Float and String are open domains and
are not exhaustively enumerated. Refined finite domains that cannot be proven
complete, and finite domains exceeding the bound, block library qualification. Full details are in the
[finite coverage contract](FINITE-COVERAGE.md).

Documentation and examples remain separate metadata; the runtime library gate
does not itself require them. Experiments may explicitly require and
independently check them. Coverage establishes execution and enumerated cases,
not that the operation satisfies every boundary or domain rule. The latest test
batch replaces current coverage; use `test-all` before inspecting several words
together. Run examples explicitly as an additional check.

## Replacing a definition

Inspect `describe.revision` first. Replacing source is a staging operation with
an explicit expected revision:

```json
{"op":"define","source":"<complete replacement source>","replace":true,"expectedRevision":1}
```

Use the current observed revision, not a guessed value. A stale revision fails
without changing the word. Preserve existing tests/examples unless deliberately
editing them under the documented attachment rules.

For a new candidate, stage its replacement with this request, run its tests,
then use normal `commit`. For a committed word, stage the replacement with this
request, test it, then use `replace-word` to accept the staged revision after
the affected caller tests pass. `replace-word` does not accept replacement source
and is not the command for revising an uncommitted candidate. Temporary words
are task-scoped vocabulary; local bindings are values inside one invocation.
Promotion controls lifetime separately from library quality.

For a coordinated signature change, stage the complete replacement functions in
one multi-declaration Flow document:

```json
{"op":"define","frontend":"flow","syntaxVersion":2,"source":"<complete replacement functions and inline cases>","replace":true,"expectedRevisions":{"subscription.handoff":1,"caller.one":1,"caller.two":1}}
```

The map must name exactly the functions declared in the source and use their
currently observed revisions. This replacement batch accepts existing persistent
Flow functions only. It preserves identities and library maturity, advances each
revision, and checks the complete proposed dictionary before staging any change.
Record, scalar and enum schemas are immutable after creation. A `define` request
with `replace=true` and any type declaration fails with
`FLOW_PROJECT_REPLACEMENT_TYPES_UNSUPPORTED`, including type-only and mixed
type/word documents. The diagnostic identifies the first declared type and its
source span. `expectedRevision` and `expectedRevisions` compare word revisions
only; they cannot change or bypass a type schema. Without `replace=true`, a
duplicate type continues to fail as `FLOW_PROJECT_TYPE_ALREADY_EXISTS`. Define a
new type under an unused name and migrate dependent words separately. New
functions, temporary definitions, attachment removals and external attachment
arrays are also outside this replacement operation. Use the existing scalar
`expectedRevision` request for a single-function edit.

Inline standalone tests/examples replace matching cases under their owner's
revision check; omitted cases remain and must still type check. Existing
`test-file` context is retained. A standalone case cannot replace a wrapped case,
and new wrappers are outside this initial batch operation. If an omitted test,
example or caller still uses the old signature, the entire request fails.

Run the affected tests, then `replace-word` on the changed callee. Publication
includes staged affected callers and requires their tests and library
qualification before publishing together. Ordinary `commit` aliases apply the
same caller selection when they select a persistent replacement. Unrelated batch
members outside the selected dependency/caller closure remain staged. Staging
does not persist the batch, and a failed publication leaves the durable project
unchanged. Task abort retains its normal rollback behavior.

These are JSONL protocol operations. The six-tool model harness currently has
a smaller mutation adapter and does not expose the full replacement protocol.
Only use operations supplied by the active host; consult help for supported
fields and source forms rather than inventing extra fields.
