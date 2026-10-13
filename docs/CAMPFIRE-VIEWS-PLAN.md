# Language-owned Campfire views

Updated 2026-10-13. Goal: AgentLang renders the full Rails room page itself, so the
[report 259](../reports/259-campfire-room-benchmark.md) comparison against
Rails and Rust becomes behaviourally apples-to-apples. The benchmark is rerun
unchanged afterwards: caching disabled everywhere, the harness contract, and the
same placement.

## Decisions (user, 2026-10-13)

- **Views live in the language.** Flow computes view data and decisions, and the
  HTML templates are language code. The compiler and stdlib gain what that needs.
  The F#/C renderer twins are retired for each part the language takes over.
- **Strings are immutable.** Strings are bump-allocated on the request's data
  stack and reclaimed when the request ends. Allocating several is cheap, so a
  page is built from separately allocated pieces combined by a stdlib
  `string.join`: it sizes its result once and copies each piece once, so a few
  levels (fragments, then messages, then page) copy a linear multiple of the
  page size. Repeated `concat` of a growing page (quadratic) is avoided. The
  compiler may later prove last-use appends and extend in place as an
  optimization; that does not change semantics.
- **Stdlib improvements are in scope** wherever the views need them.
- **`compact { }` is the only relocation mechanism.** The planned
  `let result = compact { ... }` (PRD; `STABLE-ARENA-LOWERING.md`) is
  implemented as the explicit, compiler-proven way to move a block's surviving
  results to its region mark and rewind its temporaries. List operations never
  compact implicitly:
  - `map` builds a contiguous list. When each callback result directly follows
    the previous one, which a `compact { }` callback body produces, it copies
    nothing; otherwise each result is copied once.
  - `fold` returns the last accumulator without copying.
  - `filter` copies each kept element once.

  Temporaries otherwise remain until an enclosing `compact { }` or request end.
- **No instruction limit in the AOT runtime.** Verified programs have acyclic
  call graphs and only finite list traversal, so they terminate. The
  interpreter's bound becomes an execution option, with unbounded execution for
  AOT parity runs.
- **Benchmarks** compare AgentLang AOT with Rails and Rust. The interpreter is a
  parity control only. Cache-enabled comparisons come later.

### Decisions (user, 2026-10-13, after report 262)

- **Templates:** improve them dramatically, aiming for both readability and
  efficiency. Both styles are wanted: an interpolated literal (`html"…{x}…"`) for
  one-liners and a template block (`html { … }`) for partial and page bodies.
  Each template lowers to one sized join. Interpolated `String`s are escaped and
  `Html` is inserted as-is.
- **Native list building** (`list.append`, `list.concat`).
- **`Html.new` is always checked.** Literals are checked at compile time, and
  every other construction is checked at run time.
- **UTF-8 internal strings.**
  - Length counts bytes and is renamed `string.byte-length`.
  - A separate function gives the character (Unicode scalar) length.
  - Invalid UTF-16 arriving from hosts (unpaired surrogates) is rejected at the
    boundary.
- **Bulk range copies and better structure checks** (proven structural access).
- **Straight-to-socket responses:** explore a readable and efficient design for
  fresh and cached results ([RESPONSE-STREAMING.md](RESPONSE-STREAMING.md)).
- **Kept for later review** ([PENDING-REVIEW.md](PENDING-REVIEW.md)): immutable
  strings and workflow ceremony.
- **Raw vs escaped HTML: one type, explicit raw.**
  - `Html` remains the only markup type.
  - Run-time markup enters only through a distinctly named `html.raw(text)`, so
    `Html.new` can't be called on run-time strings.
  - Literals (including template parts) are validated at compile time, and their
    run-time check is skipped.
  - Enforcement will be a generic feature: a refined type's constructor can be
    literal-only outside the type's own namespace.
- **No list length limit:** the 10,000-element cap is dropped; memory bounds
  lists.
- **Shared immutable literals:** each String or proven `Html` literal is emitted
  once into the binary's read-only data and referenced by every worker, with no
  per-request copy and no locks. Native descriptors need to address this static
  region alongside the request arena. **Deferred (user, 2026-10-13):** this
  comes later, and only after further testing.

## Constraints

- The entry is never replayed to size output, because it may refresh a session.
  Language views produce the body string inside the single entry execution, so
  there is no measure-then-render replay.
- **Safety is a type rule.** `Html` is a library refined type over `String`
  (user decision 262), not a compiler built-in. Views take raw markup only from
  literals, which are validated at compile time. Runtime strings enter through
  `html.escape`, which matches Rails ERB escaping byte for byte (Rails uses the
  same five-character rule for text and attributes). `Html.new` on a runtime
  string is validated by the type's policy at run time. A lint restricting it in
  view modules is open.
- No new locks or cross-worker dependencies. The request stays on its worker.
- Semantic IR stays authoritative. The interpreter implements the same semantics.
- Each view milestone records structural locates per GET (Detailed profile).
  Report 256 found about 3.9 µs per locate and about 2 µs per type-graph
  validation.

## Parity oracle

1. Per response: the shared harness `room_show` contract, as in reports 258/259.
2. Per page: a normalized structural comparison with the real Rails page on the
   harness seed. It masks per-request values (CSRF tokens, nonces) and compares
   element structure, attributes and text.
3. Vectors from the Rails app via the Rust port's `reference-tools`
   (`rails_compat_vectors.rb`, `richtext/generate.rb`): escaping, time
   formatting, and the rich-text sanitizer.

## Order

1. **AOT list operations** (done, [report 260](../reports/260-aot-list-traversal-no-instruction-limit.md)). `ListSingleton`, `ListMap`, `ListFilter`, `ListFold`
   and `ListEach` in the owning backend, without implicit relocation, with
   interpreter parity at O0/O2. Remove the AOT instruction limit. Templates need
   these to iterate messages and boosts.
1b. **`compact { }`** end to end: done in [report 261](../reports/261-compact-blocks.md).
   A compact map callback cut peak arena use by 65% and let the map build its
   list in place.
2. **`string.join` and the `Html` surface** (done, [report 262](../reports/262-string-join-html-escape.md)).
   Single-copy `string.join`, Rails-exact `string.html-escape`, and native
   `string.contains`/`starts-with`/`ends-with`. `Html` is a library refined type
   (user decision); refined literals are validated at compile time. The 40 × 9.6 KB
   prototype copies 6× the page because rows and the page use `concat` chains.
   Native `list.append`/`list.concat` would let each level use one join (about 3×).
   µs per GET was **not** measured: the trusted profile needs a host adapter.
   The checked harness spends about 65 ms per page above its floor. Next is a trusted
   measurement to choose between list building and faster copies.
2b. **Native list building** (done, [report 263](../reports/263-list-building.md)):
   `[a, b, c]` literals, native `list.append` and `list.concat`, and fused
   literal joins.
2c. **Near-term copy removals** (user, 2026-10-13):
   - **Free nominal list retag.** Unwrapping `List<Html>` to `List<String>`
     (`parts.map(html.text)`) copies today. A map whose callback is only a
     nominal unwrap or wrap should retag the list descriptor, with no copy.
   - **No unneeded argument copies.** Audit call-argument passing so values
     already on the stack are passed by descriptor, not copied into the callee.
   - **In-place `list.append` / `list.concat`.** When the list is the newest
     value on the arena and nothing else refers to it, extend it in place and
     copy only the new element.
3. **Proven structural access** for statically validated layouts, re-measured
   with the report-256 ABBA method. Runtime checks stay for unproven paths.
4. **Message partial** in the language. New host operations supply the creator
   (name, title, avatar token) and boosts. Message policy (already in Flow)
   drives the edit action, and the day separators, local-time tags, permalinks,
   quick boosts and actions menu follow. Validated by the contract and the
   structural oracle.
5. **Page layout, sidebar and composer.**
6. **Rich text.** Rails-equivalent sanitization (a trusted adapter, checked with
   vectors), attachments and mentions. Until then, escaped bodies are a named gap
   with an estimated cost.
7. **Signed avatar URLs** through the worker's existing crypto context.
8. **Rerun the report-259 protocol** unchanged and report it.

Each step is its own tested, reported and pushed milestone. Anything Rails or Rust
does per request that AgentLang still skips is listed as a named gap in the
benchmark report.
