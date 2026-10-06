You work through a JSONL protocol in a deterministic typed language. Start the
exact supplied trial host once and send one JSON request per line through that
same returned session. AI agents are external users; the runtime contains none.

Use compact inventory or structural search to discover names:

```json
{"op":"words","compact":true}
{"op":"search","query":"customer"}
```

`describe` returns detailed word metadata and source. `context` returns bounded
signature/effect/dependency/documentation/type metadata for a word and its
closure, omitting whole entries when necessary. Check omission counts. It has
`maxDepth`, `maxWords` and `maxUtf8Bytes` fields; the latter bounds its complete
data JSON, not the response wrapper. Both return parser-verified `flowReference`
call spellings. Use dictionary names for protocol queries, declarations and
test owners; use call references for expression targets. Root `add` is `::add`;
`numbers.increment` is `numbers::increment`. Dot chaining passes a receiver as
the first input. A null reference has an explanation.

Flow uses named typed inputs, immutable `let` bindings, ordinary calls and no
implicit nominal conversions. Both branches must produce the declared result:

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
{"op":"task.begin","goal":"<public task>"}
{"op":"define","frontend":"flow","source":"<word and attached tests>"}
{"op":"test","word":"increment"}
{"op":"commit","word":"increment","library":true}
{"op":"task.commit"}
```

Tests belong to words through the slash separator. These tasks require pure
retained library words with passing own tests and complete own instruction and
branch coverage. Caller tests cannot replace helper coverage. Add tests using
an attachment-only Flow document when useful. Preserve prior types, helpers and
tests. Use only the supplied protocol; do not read language project files.

The same host operation allowlist and inspection-response byte cap apply to all
actors. Whole inspection responses may be denied by that cap; no partial JSON
is supplied. A denial after inspection does not mean the query was unexecuted.
Mutations and tests remain visible and are outside the inspection cap. Never
automatically repeat an uncertain request. Observe the same live session or
report the uncertainty for coordinator recovery. Model tokens and context
windows are not measured by this cap.

Report completion after public tests and durable task commit, with actual
outcomes, errors, missing capabilities and the host session ID. Leave that
session open; the coordinator handles teardown separately. Do not send quit
commands or control characters through JSONL. Do not spawn other agents.
