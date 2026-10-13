# 261 — `compact { }` blocks and precise arena-lifetime modeling

Status: language and compiler milestone toward language-owned Campfire views
([plan](../docs/CAMPFIRE-VIEWS-PLAN.md)). No application or benchmark change.

## `compact { }`

Following the user's design (PRD; `STABLE-ARENA-LOWERING.md`), Flow/2 now has an
explicit, opt-in compaction expression:

```flow
let label = compact {
    let text = int.to-string(value)
    string.concat(string.concat("#", text), "!")
}
```

- **Shape.** The block must produce exactly one value (`FLOW_COMPACT_RESULT`,
  `TYPE_COMPACT_RESULT`, `IR_COMPACT_SHAPE`). A record carries several.
- **Interpreter.** Identity semantics: the block is evaluated as a scope.
- **Owning AOT.** The emitter records the block's arena mark and evaluates the
  block.
  - A result allocated inside the block is moved down to the mark
    (`al_owning_move_range`, which tolerates overlap), and everything above it
    is released.
  - A result older than the mark, such as an outer value, stays in place and
    the block's temporaries are released.
- **Proof, or a compile error.** The arena-lifetime analysis must show that
  nothing other than the single result can refer to the block's region: no
  outer stack value, surviving local or effectful or unmodeled operation.
  Otherwise compilation fails with `IR_OWNING_COMPACT_UNPROVEN` and the
  analysis reason. Nothing is decided at run time.
- **Other backends.** The fixed-layout owning emitter and the scalar backend
  never rewind, so `compact` is a plain scope there.
- **Language surface.**
  - IR `Compact`, with formatting, fingerprints and the vocabulary
    serialization tag.
  - `compact` is a reserved word.
  - Source rendering, formatter, rename-rewrite paths and lint.
  - Stored AST paths (`compactStatement`).
  - Authoring help and `CURRENT-SYNTAX.md`.

Ordinary scopes, function returns and list operations still never relocate.

## Precise arena-lifetime modeling

`compact` relies on the arena-lifetime analysis. That analysis previously
treated scalar wrap/unwrap, enum construction and matching, and all list
operations as unmodeled, marking them as possibly retaining any active region.
For `Scope` rewinds that was only conservative, but for `compact` it would have
rejected almost every realistic view block. They are now modeled precisely:

- `UnwrapScalar` projects its input's origins.
- `WrapScalar` and `MakeEnumCase` create values in the current region, joined
  with their input origins.
- `MatchEnum` joins its arms like `If`.
- `ListSingleton` creates a new owner holding a copy.
- `map`, `filter`, `each` and `fold` follow the user-call rule: output origins
  are the inputs plus the active regions, and only an effectful callback
  escapes.

Effectful wraps and validators still fall back to the unmodeled path. Scope
rewinds therefore become more permissive only where values are provably dead.
New tests cover:

- dropped versus escaping singletons and maps;
- effectful callbacks;
- pure and effectful compact blocks;
- relocated compact results inside an enclosing scope, both dropped and
  escaping.

## Measured effect

For the same 40-element map of `Int → "#N!"`, with and without `compact` in the
callback (O0 and O2):

| | Plain callback | `compact` callback |
| --- | ---: | ---: |
| Peak arena cursor | 4,824 bytes | **1,680 bytes** (−65%) |
| Map copies (fallback copy-once path) | 640 bytes | **0** (list built in place) |
| Moves | 0 | 640 bytes (each result moved once) |
| Callback-internal copies (`string.concat`) | 2,560 bytes | 2,560 bytes |

This confirms the report-260 expectation: a `compact` callback body makes the
map build its list in place. Without `compact`, nothing moves and each result
is copied once.

## Validation

All 15 gates passed in one clean run on the final source
(`gates-20261013T135957Z`). The whole solution builds with zero warnings.

| Gate | Result |
| --- | --- |
| Full LLVM suite | 1,009 assertions |
| Arena-lifetime analysis | 55 assertions (was 44 before this milestone) |
| New `compact` conformance | 48 assertions (O0/O2) |
| Flow syntax and lowering | 1,329 assertions |
| Flow runtime | 2,564 assertions in 47 groups |
| IR interpreter | 161 assertions |
| Owning list ops / owning lists | 142 / 1,725 assertions |
| Campfire regression gate | pass |
| Native value-stack, owning mailbox, IR parity, native conformance | pass |
| Campfire native room-read, TrustedGenerated and Diagnostic | 238 assertions each |

The `compact` suite checks, at O0 and O2:

- authored Flow tests pass (interpreter identity semantics);
- native results equal the interpreter;
- the map measurements above, with exact move bytes;
- an outer value is returned unchanged with no move;
- a projected field of an in-block record is relocated;
- nested blocks compose;
- canonical formatting keeps `compact { }` and is idempotent;
- committed `compact` words reload in a fresh engine and pass their tests.

Unprovable blocks are rejected at compile time, but no pure Flow program can
exercise that error. Inside the owning subset only effectful operations defeat
the proof, and effects require a native host profile; outside the subset,
record validators are rejected before lifetime analysis runs. The rejection is
therefore tested in the arena-lifetime suite: a compact block containing an
effectful callback is not proven.

## Next

`string.join` and the `Text`/`Html` surface, then proven structural access, then
the message partial in the language (views plan steps 2–4).

## Sealed evidence

[Archive](evidence/261-compact-blocks/261-compact-blocks-evidence.zip) and
[manifest](evidence/261-compact-blocks/manifest.json): 51 entries, 576,262 bytes,
SHA256 `6c9b14882d9ac48030bbb100b1b27313a69aac5c57778b794fff66bf7d049eaf`. It
holds the final gate logs and summary, the `compact` suite receipts with
per-case copy/move/peak metrics, the gate and sealing tools, and snapshots of
every changed source, test and document.
