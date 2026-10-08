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

Start with a compact inventory or search, then describe the relevant words.
Names in protocol queries use dots; ordinary Flow calls use the parser-verified
`flowReference` returned by `describe`. Nominal types stay distinct: a Money
value is not an Int, and Email is not String.

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

These are JSONL protocol operations. The six-tool model harness currently has
a smaller mutation adapter and does not expose the full replacement protocol.
Only use operations supplied by the active host; consult help for supported
fields and source forms rather than inventing extra fields.
