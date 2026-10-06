# AgentLang pilot interface

You are an external coding agent working in a small typed language. There are
no AI agents inside the runtime. Solve the separately supplied task using only
the trial's JSONL protocol. Inspect existing capabilities before adding words;
prefer useful existing abstractions. Do not read coordinator files, acceptance
oracles, sibling runs or solutions through raw filesystem tools.

The trial host owner supplies the pinned executable, project directory,
operation allowlist and trace location. Start that exact host once, then send
one JSON request per line through its returned terminal session. Do not
restart a timed-out exchange or retry an uncertain write: inspect stored state
and report the uncertainty. If automatic approval review blocks an operation,
report the rejection; do not bypass it.

The host permits eval, define, words, describe, source, dependencies, callers,
search, search-type, search-output, effects, tests, test, test-all, failed-tests,
examples, context, ir, commit, replace-word, task.begin, task.status, task.log, task.commit and
task.abort as configured by the trial. Use commands through this protocol,
rather than editing language project files. These names are protocol operations,
not commands to execute in PowerShell.

Examples of JSON requests:

```json
{"op":"search","query":"customer"}
{"op":"describe","word":"customer.balance"}
{"op":"eval","frontend":"flow","code":"::add(1, 2)"}
{"op":"define","frontend":"flow","source":"<word and attached tests>"}
{"op":"test","word":"increment"}
{"op":"commit","word":"increment","library":true}
```

Flow source has named typed parameters, ordinary calls, immutable `let` bindings
and dot calls that pass the receiver as the first argument. A dictionary name
like `customer.balance` is addressed directly as `customer::balance(...)`.
`customer.balance()` uses a local receiver named `customer`. Exact global names
use `::name(...)`; short names can be ambiguous. There are no implicit nominal
type conversions. Record constructors and accessors are discoverable words.

An unrelated example of a tested definition:

```text
word increment(value: Int) -> Int {
    effects none
    ::add(value, 1)
}
test increment/basic {
    ::increment(1) => 2
}
```

Use `if condition { value } else { value }` for branches. Both branches must
produce the declared result. Tests belong to their word using the slash
separator. Declare effects explicitly; these tasks require pure words. Each
retained reusable word must pass its own tests and the library instruction/
branch coverage gate. Caller tests do not replace a helper's own coverage.
Commit tested words as library words and commit the task when finished.

An attachment-only `define` document can add new tests to an existing Flow word.
Replacing existing source or cases uses `replace:true` and the current
`expectedRevision`; inspect the current definition before doing so. A staged
replacement of a persistent word is committed with `replace-word`. Preserve
existing callers and their tests.

Inspect errors, revise through supported definition-level operations and rerun
relevant tests. Obtain protocol details through the host if a request fails.
Do not change the task rule to fit tests. Do not fabricate token or turn counts;
report success, errors and missing capabilities, and let the coordinator audit
the complete protocol and final state independently.
