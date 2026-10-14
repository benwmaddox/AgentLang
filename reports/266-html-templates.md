# 266 — HTML templates, built-in Html, and a Flow room page

Status: language, compiler, runtime and Campfire app milestone (views plan step
2). The Campfire room page now exists as a Flow template that matches the
C-rendered page byte for byte, apart from escaping. The host still sends the
C-rendered body; report 267 switches it over.

## User decisions

- **Templates are interpolated literals only:** `html"…"` and multi-line
  `html"""…"""`.
  - The whole markup is checked at compile time.
  - Every interpolated `Html` is a balanced snippet.
  - The `html { … }` block is dropped.
- **`Html` is built in** (decided during this milestone):
  - **`Html.new(text)` escapes everything** and cannot fail.
  - **`html.raw(text)` validates and keeps markup:** literals at compile time,
    other values at run time.

  This replaces the earlier rule "`Html.new` is always checked". Under that
  rule `Html.new` and `html.raw` did the same thing, and the obvious call did
  not escape.

## Language

- **Syntax.**
  - `html"…"` is one line with JSON escapes.
  - `html"""…"""` is raw and may span lines.
  - `{expr}` is a hole, and `{{`/`}}` are literal braces.
- **Multi-line text (C# raw-string rule).**
  - The newline after the opening `"""` and the closing `"""` line are dropped.
  - The closing line's indentation is removed from every line.
  - CRLF reads as LF.
  - Lines indented less than the closing delimiter are rejected
    (`FLOW_TEMPLATE_INDENT`).
- **Holes.**
  - A `String` hole is escaped exactly like Rails `ERB::Util.html_escape`.
  - `Html` and `List<Html>` holes are inserted as-is.
  - Any other type is `FLOW_TEMPLATE_HOLE_TYPE`.
- **Compile-time skeleton check (`FLOW_TEMPLATE_MARKUP`).** It runs over the
  literal parts, with each hole treated as an opaque, balanced unit:
  - elements are balanced and closed in order;
  - a bare `<` is rejected;
  - a String hole is allowed only in content or a quoted attribute value;
  - an Html hole is allowed only in content;
  - no hole is allowed in a tag or attribute name, an unquoted value, a
    comment, or `<script>`/`<style>` text.
- **Built-in `Html`.**
  - Its declaration (`type Html : String { validate string.html-fragment? }`)
    is added to the first Flow/2 document that uses `Html`, `html.raw` or a
    template.
  - Programs that never use it are unchanged: no type IDs shift, so no frozen
    fixtures move.
  - Programs may not declare `Html` or `html.raw` (`FLOW_HTML_BUILT_IN`).
- **`string.html-fragment?`** is a new primitive: the same checker at run time.
- **Formatting.** `fmt` renders templates back and is idempotent.

## Compiler and runtime

- **A new IR operation, `HtmlTemplate(htmlType, parts)`.**
  - The verifier re-runs the skeleton check and the hole types, and requires
    the target type's validator to be `string.html-fragment?`.
  - The result is therefore `Html` by construction, and no backend runs the
    validator on a template. `Html.new(x)` lowers to the same operation as
    `html"{x}"`.
- **Native lowering.**
  - `al_owning_html_template_plan` measures the literals, the holes and the
    escape expansion.
  - The result is reserved once.
  - `al_owning_html_template_write` writes every byte exactly once.
  - Literal text is copied straight from read-only data; it is never staged as
    an arena String.
  - No escaped temporary exists.
  - Measured: a template's deep copy equals exactly its result extent.
- **The C fragment checker** ports the F# one. The two agree on a 41-case corpus
  and on 3,000 random markup strings (1,500 at each of O0 and O2). Nesting is
  limited to 256 elements in both, because the C version uses a fixed array.
- **The validator dispatch** recognizes the built-in validator, which runs
  natively without a user-word call.
- **The scalar LLVM backend** rejects templates (`IR_LLVM_HTML_TEMPLATE_UNSUPPORTED`).

## Runtime fix: an 8 KB clear on every value measure

Profiling the Flow room page showed about 2 µs per `map` element, almost all of
it in `al_owning_validate_type_graph`.
- **Before:** it cleared two 4,096-entry memo arrays with volatile byte stores
  on every call. That is 8 KB of writes per value measure, field locate and
  typed comparison, although a program has only a few dozen types.
- **After:** it clears only `layout->type_count` entries. Every memo read is
  already guarded by `index < type_count`.

Measured (trusted profile, O2, native time only, median):

| Case | Before | After |
| --- | ---: | ---: |
| Entry with 40 records, returning 0 | 16 µs | 14 µs |
| `join(map client-id)` over 40 records | 176 µs | 35 µs |
| Template `map` over 40 Strings | 81 µs | 10 µs |
| Template `map` over 640 Strings | 1,260 µs | 163 µs |
| Room page, 40 messages (16.6 KB) | 537 µs | 124 µs |

The fix applies to every native program, Campfire's request path included. Its
effect on the HTTP benchmark is below.

## Campfire: the room page as a Flow template

`applications/campfire/views.flow` ports the C renderer's room page:
`campfire.views.message`, `campfire.views.room` and `campfire.views.room-text`.

- **Against an independent F# statement of the C renderer's markup** (with Rails
  escaping): equal in the interpreter, at O0 and at O2, on tricky inputs (quotes,
  `<script>`, emoji, an empty room, 40 messages).
- **Against the real C page.** The private snap-nc capture of the C-rendered
  page (`native-265.html`, 10,073 bytes, not committed) was parsed back into its
  room and messages and rendered by the Flow view. **The Flow page is
  byte-identical to the C page** after the C encoder's numeric entities are
  normalized to Rails escaping: `&#x27;` → `&#39;`, and `&#x2013;`/`&#x2014;` →
  raw UTF-8. Only hashes are recorded.
- **Named gap.**
  - The C encoder is not Rails-compatible: it writes `'`, `+`, controls and
    all non-ASCII characters as `&#xNN;`. The Flow view is the closer match to
    Rails.
  - Message bodies are rich text (`<p>…`). Both renderers escape them; Rails
    renders them as markup. That is the message-partial step.

## Tests

- **New: `AgentLang.OwningTemplates.Tests`,** 3,561 assertions.
  - **Rejections:** 20, each by its diagnostic code.
  - **Parity:** Rails escape vectors, multi-line, braces, empty and hole-only
    templates, `List<Html>` holes (0, 1, 3 and 40 items) and `Html.new`
    escaping, in the interpreter and native O0/O2.
  - **Copy accounting:** deep copy equals the result extent.
  - **`html.raw`:** accepted and rejected in both backends.
  - **Fragment checker:** the C/F# parity corpus plus the random corpus.
- **New: `AgentLang.CampfireViews.Tests`,** 12 assertions: the oracle, the
  captured C page, and the timing above.
- **`AgentLang.OwningHtml.Tests`:** its library refined type is renamed from
  `Html` to `Markup`, because `Html` is now built in. Its 359 assertions still
  pass.

## Performance (report-259 protocol, ABBA)

This uses the new per-batch quick benchmark (`.agentlang/campfire-bench/Quick-Bench.ps1`).
- **What it runs:** only the AgentLang native host, on the fixed room-page GET
  (snap-nc, 2 s warmup at 4 clients, 8 s at 16 clients, gzip off), twice; the
  median is recorded.
- **What it compares against:** the previous AgentLang record, and the
  recorded report-259 Rails and Rust runs, which are not rerun because their
  code does not change.
- **Host build:** trusted runtime, O2, built from a fresh room-read
  validation run of these sources.

| Server | Page bytes | Req/s | CPU / req | p99 | Peak working set | Peak private |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| **AgentLang 266** | 10,073 | **9,008** | **0.43 ms** | **2.1 ms** | 11.9 MB | 46.2 MB |
| AgentLang 265 | 10,073 | 7,603 | 0.51 ms | 2.6 ms | 11.9 MB | 46.2 MB |
| Rust, identity (recorded) | 416,127 | 1,322 | 2.93 ms | 25.9 ms | — | — |
| Rails, identity (recorded) | 463,753 | 19 | 176 ms | 1,108 ms | — | — |

- **Change:** +18.5% req/s, −16% CPU per request and −21% p99 against 265, with
  memory unchanged and 0 invalid responses.
- **Cause:** the request path still renders HTML in C, so this is the memo-clear
  fix (every value measure and field locate), not templates.
- **Discarded:** a run measured while the gates were running (7,873 req/s,
  p99 4.5 ms) is marked contaminated in the history.
- **Not equivalent:** the pages differ, so no speed ratio against Rails or Rust
  is claimed.

## Gates

All 21 gates pass (run `gates-20261013T235040Z`, 28 min).

| Gate | Result | Seconds |
| --- | --- | --- |
| llvm-full-suite | pass | 286 |
| arena-lifetime | pass | 0 |
| owning-compact | pass | 17 |
| owning-html | pass | 40 |
| owning-listbuild | pass | 22 |
| owning-utf8 | pass | 8 |
| owning-templates | pass | 16 |
| campfire-views | pass | 8 |
| acceptance | pass | 9 |
| flow-tests | pass | 7 |
| flow-runtime-tests | pass | 29 |
| ir-interpreter | pass | 4 |
| campfire | pass | 274 |
| native-value-stack | pass | 305 |
| owning-mailbox | pass | 70 |
| ir-parity | pass | 13 |
| native-conformance | pass | 286 |
| owning-lists | pass | 11 |
| owning-list-ops | pass | 20 |
| room-read-trusted-off | pass | 153 |
| room-read-diagnostic-off | pass | 152 |

Two earlier runs were stopped part-way: source changed mid-run (the built-in
Html decision, then the callback rule).

## Next (report 267)

1. **General app-declared host capabilities.**
   - Remove the hard-coded Campfire profiles (`CampfireRoomFindForUserV1`,
     `CampfireRoomReadV1`, and the `CampfireHttpResponse`/`HttpView` field
     table) from the compiler.
   - The response body becomes an app-declared field the host writes to the
     socket.
2. **Switch the host to the Flow-rendered body** and delete
   `campfire_http_renderer.c`. Measure what that costs per request.
3. **Then the full page:** the message partial (rich text), then layout, head
   and composer.
