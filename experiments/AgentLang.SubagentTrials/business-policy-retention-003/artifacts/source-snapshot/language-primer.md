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
an attachment-only Flow document when useful. Preserve prior types, source and
tests. Use only the supplied protocol; do not read language project files.

Documentation belongs inside word source as `doc "..."`; comments do not
populate documentation metadata. Bare `=>` expectations use supported
literal values. Tests also support `=> value expression` and `=> error CODE`;
examples require literal expectations. For nominal Money, unwrap it with
`Money::value(...)` when comparing against an Int literal.

Use runtime help over this raw JSONL protocol: `{"op":"help"}` selects the
authoring overview; topics include `define`, `replacement` and `examples`. The
host explicitly allows `help`. The version-1 host's optional inspection
classifier does not include help; no cumulative inspection-response budget is
enabled here. A help call is available but is not required for acceptance.

To replace source, inspect the current revision, then define with
`replace:true` and that `expectedRevision`. For a new candidate, test it and
use normal `commit`. For a persistent word, test its staged replacement and
affected callers before `replace-word`; that operation accepts no source and
persists the staged version. Follow the active host's operation allowlist.

`describe.coverage` reflects the most recent test batch. Testing one word
replaces that batch; another word can then display `not-run` even when its own
earlier suite passed. Use the returned `test` coverage for that run, or
`test-all` before inspecting several words' current own coverage together.

Protocol-only editing is an instruction boundary, not an OS sandbox. The
100-exchange host bound and per-exchange deadline apply. Observe the same live
session across timeouts; never automatically repeat an uncertain request.
Protocol bytes are not model tokens or context windows.

Report completion after public tests and durable task commit, with actual
outcomes, errors, missing capabilities and the host session ID. Leave that
session open until independent acceptance completes. Do not send quit commands
or EOF. Do not spawn other agents.
