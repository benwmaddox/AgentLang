You work through a JSONL protocol in a deterministic typed language. AI agents
are external users, not runtime entities. Start the exact supplied trial host
once and send one JSON request per line through its returned session.

Use compact inventory or structural search, then describe selected words:

```json
{"op":"words","compact":true}
{"op":"search","query":"customer"}
{"op":"describe","word":"customer.balance"}
```

Compact `data.words` and `data.constructs` contain names. Full `describe` exposes
signature, effects, identity, lifecycle, dependencies, tests and source. Its
`flowReference` is the exact source spelling; protocol requests still use the
dictionary name. For example `customer.balance` is `customer::balance(...)`,
while root `add` is `::add(...)`. Dot calls pass a receiver as the first input.
There are named typed inputs, immutable `let` bindings and no implicit nominal
conversions. Branches use `if condition { value } else { value }` and both must
produce the declared result.

```text
word increment(value: Int) -> Int {
    effects none
    ::add(value, 1)
}
test increment/basic {
    ::increment(1) => 2
}
```

```json
{"op":"define","frontend":"flow","source":"<word and attached tests>"}
{"op":"test","word":"increment"}
{"op":"commit","word":"increment","library":true}
```

Tests belong to words through the slash separator. Retained library words
require passing own tests and complete own instruction/branch coverage; caller
tests cannot replace a helper's coverage. These tasks require pure words.
Begin a task using `task.begin` with `goal`, and finish durable work with
`task.commit`. Inspection, eval, source, dependencies/callers, search/type/output,
effects, tests/test/test-all/failed-tests, examples, context and ir are available
according to the host allowlist. Requests may use top-level fields or nested args.

To add tests to an existing word, define an attachment-only Flow document.
To replace source, inspect its current revision and define with `replace:true`
and `expectedRevision`, then `replace-word` to persist it. Discover operation
contracts through errors; do not edit language project files directly. Library
test gates and independent coordinator acceptance serve different purposes.
