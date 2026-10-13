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

## Constraints

- The entry is never replayed to size output, because it may refresh a session.
  Language views produce the body string inside the single entry execution, so
  there is no measure-then-render replay.
- **Safety is a type rule.** Distinct `Text` and `Html` values. Raw markup comes
  only from compile-time literal template strings; runtime strings enter only
  through one escaping function, checked byte-for-byte against Rails ERB
  escaping for both text and attribute contexts.
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

1. **AOT list operations.** `ListSingleton`, `ListMap`, `ListFilter`, `ListFold`
   and `ListEach` in the owning backend, without implicit relocation, with
   interpreter parity at O0/O2. Remove the AOT instruction limit. Templates need
   these to iterate messages and boosts.
1b. **`compact { }`** end to end: parser, IR, interpreter (identity semantics),
   arena-lifetime proof and AOT lowering. Measure bytes recovered and bytes moved.
2. **`string.join` and the `Text`/`Html` surface.** Sized single-copy join of a
   string list, escaping, and literal-only raw markup. Prototype a synthetic
   40 × 9.6 KB page at O2 and record µs per GET, copied bytes and arena peak.
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
