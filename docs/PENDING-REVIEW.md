# Pending review

Design topics the user has asked to revisit later. Each entry says what was
decided for now and why it stays open. Add new items rather than deciding them
silently.

| Topic | Current decision | Revisit because | Since |
| --- | --- | --- | --- |
| Immutable strings | Strings are immutable and bump-allocated on the request data stack. Building a page copies pieces into joins. | Copy volume for large pages. Alternatives: last-use in-place append, or part lists written straight to the socket ([RESPONSE-STREAMING.md](RESPONSE-STREAMING.md)). | 2026-10-13 |
| Workflow ceremony | A word must have a passing attached test before commit, and Flow code goes through define, commit and test. | Feels heavy when iterating on UI templates. Kept for now; review against agent authoring experience. | 2026-10-13 |
| Per-call native overhead | Report 266 removed an 8 KB memo clear from every value measure. A 40-message room render still costs about 125 µs: field location through record scans, map callback frames, and copying list results. | Measure these per element and compare them with Rails and Rust before a full page lands. Map results are copied into the list and then into the page (three writes per byte). | 2026-10-13 |
| Rails worker count (282) | Rails runs the reference image's 3 puma workers and is wait-bound at ~0.79 of 4 vCPUs. CPU per request is the fair figure; req/s is not. | A supplementary `WEB_CONCURRENCY=4` run would let Rails use all 4 vCPUs, but it changes the reference configuration (user decision). | 2026-10-15 |
| Shared response cache | Not implemented. Caching is disabled in all benchmark servers. | A cross-worker cache is a new cross-thread dependency. Discuss it before adopting ([RESPONSE-STREAMING.md](RESPONSE-STREAMING.md#cached-parts)). | 2026-10-13 |

## Decided: forgery protection matches Rust (user, 2026-10-15)

Flow Campfire uses header-only forgery protection, like the Rust reference
(Rails main's `protect_from_forgery using: :header_only`): writes are checked by
`Origin` and `Sec-Fetch-Site` (`campfire.request.forgery-check-passed?`, which
was already the only check: no request path ever verified a token). Pages
carry no CSRF meta tags or `authenticity_token` fields, so a room page is the
same bytes on every request, and the server decrypts `_campfire_session` only
when a response changes it. Rails keeps its token scheme in the benchmark and is
labeled as the one server doing token work (report 283).

## Language and stdlib follow-ups from the worker store (284)

- **Html into the stdlib (user, 2026-10-15).** `Html` is still built into the
  compiler (`HtmlMarkup.fs`, `Compiler.fs`). Move the type and its words into
  `src/stdlib/html.flow` as a refined type over String. The compiler keeps
  only the validation: literal and template checks at compile time, and the
  proof that lets `html.raw` skip the runtime check.
- **Templates that leave elements open.** A layout can't be split into
  head/tail templates, because each half fails `FLOW_TEMPLATE_MARKUP`.
  Campfire splits the rendered page at run time instead
  (`campfire.server.page-frame`). Possible language support: a template form
  checked as a pair (head opens exactly what tail closes), or a streaming hole
  that names where the stream continues.
- **List primitives without a native lowering.** `list.tail` and
  `list.is-empty?` run in the interpreter, but the owning backend rejects them
  when it emits native code (`IR_NATIVE_HOST_PROFILE` / unsupported primitive),
  so code passes its tests and then fails the native build. 285 worked around
  it: the rich-text checker keeps its open-element stack as a path string
  (`"/div/p"`), and the server uses `list.count(x) == 0`. Lower both natively
  (a header read and an extent adjustment), then the checker can use a list
  again.
- **Host operations with no inputs.** `store.capacity`, `response.written` and
  `response.discard` take an ignored Int. Allow zero-input host operations.

## Deferred usability work (user, 2026-10-13)

Noted for after the apples-to-apples Campfire comparison.

- **Enforceable casing rule (proposal).**
  - Type names (records, refined types, enums) are PascalCase:
    `^[A-Z][A-Za-z0-9]*$`.
  - Every other name segment (namespaces, words, fields, locals, enum cases) is
    lowercase kebab-case: `^[a-z][a-z0-9]*(-[a-z0-9]+)*[?]?$`.
  - Operations generated from a type are always addressed through the type's
    exact name: `Pair.new`, `Pair.name`, `Html.new`, `Html.value`,
    `RenewalState.pending()`. Today records use `pair.new` while refined types
    and enums use type casing.
  - Enforcement: a segment starting with an uppercase letter is always a type,
    so the compiler can reject violations (`FLOW_CASING_*`) with a suggested
    fix. `agentlang fmt` rewrites old lowercase record forms during a
    migration window.
- **Naming convention, user notes (2026-10-13):**
  - Fields and functions are kebab-case with no spaces (`find-by-id`,
    `created-at`).
  - Whether namespaces and types (records, refined types, enums) are also
    kebab-case or PascalCase is **undecided**. It replaces the type-casing
    part of the proposal above if adopted.
  - **Operators need surrounding spaces.** If infix operators such as `-`
    (minus) are added, they must have a space on each side, so `a - b` is
    subtraction and `a-b` is one kebab-case name. The lexer and formatter
    enforce this.
- **CLI file workflow.** `agentlang test|check|fmt FILE.flow...` exists (basic
  version, added 2026-10-13). It uses a fresh in-memory project, maps
  diagnostics to file lines, and gives the exit code CI needs. Possible next
  steps: `agentlang run FILE word args`, watch mode, project manifests.
- **Language card.** A one-page, current summary for people and AI agents:
  syntax, stdlib list, naming rules and one complete web example.
- **`effects none` by default.** Flow/2 already treats an omitted `effects` line
  as `effects none`. Make the canonical form omit it (formatter), and update
  docs and examples so authors only write `effects` when a word has effects.
- **Web standard library** (the general host-capability mechanism itself is now scheduled on the apples-to-apples path). Typed,
  app-declared host operations (database queries, HTTP) instead of closed
  Campfire profiles in the compiler, plus routing, request/response,
  forms, cookies/sessions, JSON and time formatting.

## Trusted-profile runtime checks (report 268; review)

- **Per-instruction stack check is diagnostic-only** (user, 2026-10-14). The
  owning backend used to re-check every live stack descriptor (range below the
  cursor, init bits) after every instruction. That is quadratic in stack depth
  (about 3,400 checks per 58-hole template) and re-verifies the compiler's own
  bookkeeping, not the program. Generated IR now depends on the runtime
  profile: `Diagnostic` keeps the full check as a codegen self-test, while
  `TrustedGenerated` keeps only the boundary checks (arguments, host input,
  arena reservation) and the per-use descriptor checks.
- **Trusted `al_owning_locate_field`** walks only the fields before the target
  (bounds checked). It no longer re-validates the type graph or scans the whole
  parent on each read. The diagnostic profile keeps the full scan.
- **Report 269**, trusted builds only:
  - **Trace events:** no layout trace-event calls are emitted.
  - **Templates:** `al_owning_html_template_write` takes the plan's results
    instead of re-planning; the diagnostic build re-plans and compares.
  - **Field reads:** the location is computed inline from the static layout,
    with constants for fixed-size fields and the padded length header for
    String fields, plus one bounds check against the parent. Any other dynamic
    field still calls `al_owning_locate_field`.
- Still open: per-use descriptor `check_initialized` calls (about 4,400 per
  room GET, roughly 3 ns each in trusted) and status checks after runtime calls
  that cannot fail.

## Syntax changes, next (user, 2026-10-14)

1. **Enum cases without `case`:** one bare case name per line, or separated
   by `;`. The formatter writes the bare form, and `case` stays readable
   during migration.
2. **`expect` tests:** done (272). Flow/2 tests parse step lists with
   `expect`, `expect a == b`, an implicit final Bool, and `expect effects`;
   `applications/campfire` `.flow` tests are not migrated yet.
   - `expect <Bool expression>`; a test may hold several.
   - `expect effects { … }` asserts exact effect counts.
   - A failed `a == b` reports both values.
   - A test whose last expression is a Bool treats it as `expect <that value>`
     (user, 2026-10-14), so `test x/y { string.contains(a, b) }` is a complete
     test.
   - The old `=> literal` / `=> value …` forms stay readable during migration.

3. **Records without `field`:** a record body holds only fields, so
   `name: Type` lines are enough. `field` stays readable during migration,
   and the formatter drops it (user, 2026-10-14).
4. **Name rule:** word and namespace-segment names may not contain `(` or
   `)`. Dots stay namespace separators (user, 2026-10-14). Already true: a segment
   allows letters, digits, `-`, `_`, `?`, `!` (documented in CURRENT-SYNTAX
   "Names"). Widening to other symbols needs a user decision, since most
   symbols are operators or punctuation in Flow.
5. **Shared test overrides:** overrides declared at the top of a test file
   apply to every `test-file` block in it, and blocks may still override
   locally. An override that none of its block's tests reach is a
   diagnostic. Then collapse the copied override sets in
   `applications/campfire/*-tests.flow`. `http-tests.flow` alone repeats the
   same 10 overrides in 9 blocks, many unused (user, 2026-10-14).
6. **C-like infix operators:** done (272, user 2026-10-14). `* / % + - < <= > >=
   == != && ||` with C precedence, each written with whitespace on both sides
   (see CURRENT-SYNTAX "Operators"). No implicit conversions; `%` is the new
   `int.remainder` primitive. Unary `-`/`!` and compound assignment are not added.
7. **Loops and `var` locals:** done (272, user 2026-10-14). `var`/reassignment,
   `for i in a..b`, `for x in items`, `for x, i in items`, `break`/`continue`,
   Flow/2 statement `if` without `else`. Real native back edges, with
   per-iteration arena reclamation, and a 10M-iteration per-invocation budget
   (`RUNTIME_LOOP_BUDGET_EXCEEDED`); see CURRENT-SYNTAX "Loops and var". The arena
   LLVM backend rejects loops.

Then the stdlib restructure in report 271's "Next".

## Scheduled removals

| Item | Action | Deadline |
| --- | --- | --- |
| CSRF record fields (283) | `CampfireHttpRequest.csrf-token` and `CampfireHttpResponse.new-csrf-token` are always none since Flow matches Rust's header-only forgery protection (user, 2026-10-15). They stay only because the Windows native host (`request_encoder.c`, `campfire_http_renderer.c`, the `_csrf_token` writer in `cookie_response_codec.c`) and the .NET hosts encode those layouts. Remove the fields and that code when those hosts are deleted. | With stdlib step 5 |
| `string.length` | Deprecated alias of `string.byte-length`, introduced with UTF-8 strings. Migrate every source, test and fixture (including hash-pinned frozen fixtures, through recorded transitions) to `string.byte-length` or `string.char-length`, then remove the alias (user, 2026-10-13). Known uses at report 265: `experiments/AgentLang.NativeValueStack/value-stack.flow` (source hash pinned by its fixture), `experiments/AgentLang.OwningMailbox/owning-mailbox-refinements.flow`, `tests/AgentLang.OwningCompact.Tests/owning-compact.flow`, `tests/AgentLang.OwningListBuild.Tests/owning-listbuild.flow`. | **2026-11-01** |

## Research: compile-time evaluation (user, 2026-10-13; not yet coded)

The interpreter can already run pure code at compile time (refined-literal
proofs). Assume every pure (`effects none`) function can be called at compile
time. This may require moving much more into the interpreter first, which is
acceptable.

1. **Constant folding of call chains.** Fold a series of pure calls on
   compile-time-known inputs. Possibly with an explicit marker requesting
   compile-time evaluation (a `const`/`comptime` form) that fails the build if
   the expression cannot be evaluated.
2. **Pre-rendering templates.** Fold as much of each `html"…"` template as
   possible: constant parts, constant interpolations, and conditional or loop
   parts with known inputs. This leaves the smallest runtime work (one sized
   write of the remaining dynamic parts).
3. **Compile-time data tables.** Write a loop that builds a table and have the
   compiler expand it at compile time into a literal constant (for example
   lookup tables, emoji or reaction lists, route tables).
4. **Validated specialized types everywhere.** Refined types for many domain
   values, checked by the compiler from literals and folded values, in
   addition to run time. Proven values need no runtime check, so they stay
   efficient. Compile-time evaluation also allows better error messages that
   show the failing value, validator and source.

## Research: unused test overrides as failures (user, 2026-10-14; not yet coded)

Question: if a test (or `test-file` block) overrides a function but no test
in that scope ever calls the override, should that be a test failure rather
than a diagnostic (item 5 above)?

- For: an unused override is either dead setup or a sign the test no longer
  exercises the path it was written for; failing makes that drift visible.
- Against: shared, file-level overrides (item 5) would fail whenever one block
  does not need them, which pushes back toward copied per-block sets.
- To research: measure how often overrides go unused across
  applications/campfire tests today; decide whether the scope that must use an
  override is the test, the block, or the file; and whether a failure or a
  warning suits agent-written tests better.

## Research: in-place append by dropping dead values above the list (user, 2026-10-15; not yet coded)

When a list (or string) is appended to at its last use, the compiler drops the
dead values allocated above it, writes the new element directly after it, and
moves the arena cursor there. The list never moves; only the element is copied
(which an append copies anyway), so the append costs O(element) in any code
shape, not only in the recognized loop accumulator (278).

- Relation to existing work: the same release-and-merge the loop accumulator
  does at the latch, and the same release-to-mark mechanism as per-iteration
  reclaim and `compact { }`, but chosen by the compiler at the append site, so
  authors never write `compact` (see the no-compact rule).
- Needs:
  - liveness proof that everything above the list is dead after this point
    (except the appended element); one live value up there falls back to
    today's behavior;
  - the list itself has no other owner that could see bytes past its end;
  - an element's payload (a String element's bytes) moves down with it.
- Limits: one growing value on top at a time (interleaved builds fall back);
  no help for a list buried under live values.
- Value: straight-line appends, appends to a `let`, mixed-use loops and
  `s = string.concat(s, x)` become linear with no author effort (covers the
  "last-use in-place append" option under Immutable strings). Low value for
  the current streamed Campfire page.
- First check: whether the existing "extend in place when unaliased and built
  in the same block" path already handles the dead-above case.
- Measure: micro-benchmarks for straight-line appends, appends to a `let`,
  mixed-use loops and growing strings, against today.
- Timing: after equal pages (user, 2026-10-15).

## Native loop and call overhead (measured 2026-10-14; next backend work)

Measured with the trusted O2 build, over a 111-byte string (min of 2000 runs):

| Loop body | Per iteration |
| --- | ---: |
| empty Int `var` update | ~25 ns |
| `string.byte-at` + `match` (Option) | ~80 ns (fixed by `string.byte-at-or`: inline load) |
| call to a small Flow word | ~27 ns |

Each iteration makes 8-10 out-of-line runtime calls (load/store i64, reserve,
update live counters, release, relocate) because the runtime C is a separate
object without LTO. Plan, in order:

1. Measure the ceiling first: link runtime bitcode with the generated IR for
   `bench.count` / `bench.word-call`. Low single-digit ns means call overhead
   is the whole cost; otherwise the per-iteration work itself (reclaim,
   reserving every scalar result in the arena) needs structural changes.
2. Trusted profile only: emit the trivial helpers inline (load/store i64,
   update_live, the reserve fast path, release cursor), keeping the C slow
   paths. Diagnostic builds keep calling the checked C. Add a TrustedGenerated
   O2 pass to the native parity harnesses so the inline code is tested, not
   only benched.
3. Done (280): `inline fn` (docs/CURRENT-SYNTAX.md) marks the register-ABI
   body `alwaysinline` at O2 when its emitted body is at most 400 LLVM
   instructions; O0 never inlines. Six `campfire.ua` helpers carry it.
4. O0 frame bookkeeping: every IR temporary gets its own stack slot at O0, so
   large words exceed the 64 KB per-frame screen (lean user-agent.flow had to
   be split for this). Reuse slots or emit the bookkeeping as SSA values so
   word size stops being limited by O0 frames. (Frames are native call-stack
   bookkeeping, not the data stack.)

Lean User-Agent cost today: typical Chrome UA 50 us, corpus median 31 us
(trusted O2), versus a few us in the retired C normalizer.

## Performance opportunities (measured 2026-10-14; Flow server on Linux)

Context: the Campfire Flow server on Linux (276 smoke, 4 pinned vCPUs, no
cache) costs 3.62 ms CPU per request against Rust's 2.97 ms; the Windows
HTTP.sys host with C cookie/db code cost 1.30 ms (272). Throughput looked
level only because Flow saturated all 4 vCPUs. In rough order of expected size:

1. **Profile the Linux portable path.** Done (281); see "Linux profile (281)"
   below. The dominant cost was `db.column`, 37% uncached and 44% cached:
   each column read rescanned the whole row. Campfire's message rows now
   decode in one pass. Still open: compare measurement scopes (cgroup on Linux
   vs process CPU on Windows).
2. **`html.raw` re-validation.** Done (278). The owning backend skips the
   check when the String is provably valid Html: `Html.value` of Html
   (directly or through `let`s), literal fragments, and `string.concat` of
   those. Untrusted Strings, vars and parameters are still checked. Trusted
   O2, 21 KB page: 46.2 -> 27.5-31.1 us (the page alone is 27.0-32.2 us). A
   String from `cache.get` is not provable and is still checked.
3. **`list.append` in loops was O(n^2).** Done (278). A loop-carried `var`
   built only by `v = list.append(v, x)` is an accumulator: each append copies
   the element once onto a pending run, and the latch moves the run next to
   the list. Trusted O2, 10,000 appends: Int 20.96 -> 0.39 ms, String 40.1 ->
   0.48 ms; copied bytes 400 MB -> 80 KB; peak arena 240 -> 80 KB. The
   diagnostic profile is linear in bytes but its per-instruction self-check
   still makes its time grow with the list.
4. **Stdlib call overhead.** Done (279, trusted builds): one
   `al_owning_stdlib_call` per op validates each argument once, takes Ints by
   value, sizes formula results without a plan walk, then reserves, writes and
   accounts itself; `bytes.length` is an inline header load. Windows, trusted
   O2, ns per op: concat 63 -> 18-19, xor 76 -> 18-19, slice 63 -> 18-21,
   url-encode 62 -> 26-27, hex 75 -> 30, bytes.length 15 -> 2; masking one
   CSRF token 274 -> 177-189. Diagnostic builds keep plan/reserve/write.
5. **Word calls passed arguments through the arena.** Done (280). Internal
   words have a register ABI (`@word.reg`): scalars as i64 values, other
   arguments as their four descriptor fields, Option<scalar> results as a
   tag+payload pair; trusted builds enter/leave frames inline and skip
   proven checks. `inline fn` inlines at O2 (body <= 400 instructions).
   Small-word call loop 24 -> 2.6 ns per iteration (1.2 ns inline).
6. **OpenSSL per-call setup.** Done (279): per-thread fetched SHA-256 and
   reused EVP_MD_CTX / EVP_MAC_CTX (user approved; never shared). WSL,
   OpenSSL 3.0.13: HMAC-SHA256 0.88-0.91 -> 0.37 us, SHA-256 0.29-0.30 -> 0.12 us.
   Windows BCrypt unchanged.
7. **`Option` results through the arena.** Done (280): `string.byte-at`,
   `option.some`/`none` of scalars and word results are register tag+payload
   read directly by `match`. `match string.byte-at` 93 -> 1.8-2.0 ns per
   byte; `string.byte-at-or` stays (1.3 ns, existing code uses it).
8. **Memory.** Done (281): Flow PSS 171 MB -> 20 MB. The 171 MB came from
   `memset` committing every reservation on every worker; reservations are
   now lazy `mmap`s.
9. **Worker context copied per request.** Done (281): borrowed from the
   protected arena prefix (240 bytes, so under 0.1% of time).
10. **Native `list.get` / `list.count`.** Done (278), with interpreter parity:
    200 Strings, count 6.2 -> 3.2 us, get [150] 5.9 -> 5.6 us (about 3.2 us of
    each is input staging).

Still open from the 281 profile: field lookups that re-scan String bytes and
re-check type descriptors (~20% inclusive; take a String's length from its
prefix in trusted builds), re-validating `cache.get` hits (8.6% of cached CPU;
needs a trusted-output flag per host operation), HTML templates (~12%), arena
copies of the 460 KB page (~5%), `session-row` still on `db.column` (~1%).

280 results (trusted O2, Windows, min of 3000 runs, 999-byte input, before ->
after; another agent's test suites ran on the machine meanwhile):

| Loop body | Before | After |
| --- | ---: | ---: |
| call `fn small(Int, Int) -> Int` | 23.9-24.7 ns | 2.6-2.9 ns |
| same word marked `inline fn` | - | 1.2-1.3 ns |
| `campfire.ua.digit-end(text, 200)` (String arg) | 121-123 ns | 32 ns; 21.6 ns with inline helpers |
| `match string.byte-at` | 93-94 ns | 1.8-2.0 ns (`byte-at-or` 1.3 ns) |
| `match` on a word's `Option<Int>` result | - | 4.7-5.7 ns |
| lean User-Agent, typical Chrome UA | 33.7-36.7 us | 26.3-27.3 us; 25.3-25.6 us with inline helpers |

Per step on the small-word loop: register calling 24 -> ~3.6-4.2 ns (C frame
calls still out of line); inline frame enter/leave -> 2.6 ns (digit-end loop
38.6 -> 32-34 ns); descriptor fields as parameters and skipping proven
argument/result checks were within timer noise (0.1 us) on every probe.

Measured reference points: sum loop 0.52 ns/iteration; small-word call loop
23 ns/iteration (2.6 ns after 280); lean User-Agent 29-31 us; HMAC-SHA256 0.61 us (Windows,
BCrypt) / 1.22 us (Linux, OpenSSL one-shot); stdlib ops: concat/encode ~72 ns,
slice/xor ~125 ns, mask one CSRF token ~247 ns.

## Linux profile (281; Campfire Flow server, measured 2026-10-15)

**How it was measured.** `perf record -g -F 2999` (linux-tools 6.8) ran for
the length of the 10 s smoke, on the harness setup:
- namespace lb273, 4 pinned workers on vCPUs 8-11, loadgen on vCPUs 12-15;
- snapshot snap-nc and its session cookie, the watercooler room, `--validate`.

The binary is a trusted -O2 build made with `-Profile`, which keeps frame
pointers in the generated words. `symbols.tsv` maps the `agentlang_word_NNNN`
symbols back to Flow words.

"By Flow word" charges each sample to the innermost Flow word on its stack.
That includes the runtime and stdlib helpers the word called, and libc,
libcrypto and SQLite. libcrypto and libc have no frame pointers, so their
stacks end at that frame. Scripts, the harness copy and the raw reports are in
`.agentlang/hostperf/` of the 281 worktree.

**Before 281, uncached** (946 req/s, 4.39 ms CPU/req under perf):
- Flat by function: `al_owning_scan_value` 22.3%,
  `validate_type_descriptor` 11.9%, `scan_text` 9.5%, `scan_readable` 4.8%,
  `html_template_write` 4.2%, `copy_range` 4.0%, `al_stdlib_emit` 3.4%,
  `html_template_plan` 3.1%, `read_u64_le` 2.5%, memmove 1.2%,
  `string_join_parts_write` 1.1%, `store_i64` 1.0%, `string_join_write`
  1.0%, `publish` 1.0%, `sqlite3VdbeExec` 0.9%.
- Inclusive: `scan_value` 46.8%, `db.column` 37.0%, `locate_field` 25.3%,
  `measure_value` 13.6%, `rails-message` 11.1%, stdlib emit/write 10.9%,
  `locate_sum_case` 8.1%, `page-query` 7.3%, `al_host_flow_http_v1` 5.3%,
  `sqlite3_step` 4.9%, HMAC 4.7%, `cached-room-page` 4.6%, kernel send 2.9%.
- By Flow word: `db.column` 37.0%, `campfire.views.rails-message` 11.1%,
  `page-query` 7.0%, `cached-room-page` 4.6%, `csrf.mask` 3.6%,
  `avatar-token` 3.3%, `message-page` 2.7%, `csrf.digest` 2.0%,
  `db.optional-text` 2.0%, `db.optional-int` 1.9%, `show-room` 1.4%,
  `message-row` 1.4%, `csrf.pad-at` 1.4%, `message-fragment` 1.2%,
  `flow_http_handle` 1.1%.

**Before 281, cached `--cache-mb 32`:**
- By Flow word: `db.column` 44.4%, `page-query` 8.4%, `message-fragment`
  6.6% (of which `al_owning_utf8_valid` is 65%), `cached-room-page` 5.4%,
  `avatar-token` 3.6%, `message-page` 3.1%.
- Flat: `scan_value` 25.9%, `validate_type_descriptor` 13.3%, `scan_text`
  8.7%, `copy_range` 4.6%, `scan_readable` 4.6%, `utf8_valid` 4.4%.

**Why `db.column` cost so much.** The cost is in the runtime, which is not
281's area, so it is reported here. `db.column(row, k)` loops over
`row.values`. Every call first locates `DbRow.values`. In the trusted
profile, `al_owning_locate_field` walks each dynamic field before the target
with a full `scan_value`, and that includes the target field itself. So each
call scans the whole `List<DbValue>`, including UTF-8 validation of every
text column and the message body, then measures elements up to column k.
Campfire read about 22 columns per message row this way, times 40 rows.

281 changes `campfire.server.message-row` to make one
`for value, index in row.values` pass. That one change moved uncached CPU
from 4.39 to 2.83 ms per request under perf.

Runtime follow-ups, whose owners are the lists and locate work:
- a native `list.get` (item 10);
- a trusted-mode `locate_field` that takes a dynamic String's extent from its
  length prefix instead of scanning its bytes;
- no `validate_type_descriptor` on every locate.

`campfire.server.session-row` still reads its eight columns with
`db.optional-*` once per request. That costs about 1%.

**After 281, uncached** (prof3 build):
- Flat: `scan_value` 9.6%, `scan_text` 7.8%, `html_template_write` 6.9%,
  `copy_range` 5.6%, `validate_type_descriptor` 5.6%, `al_stdlib_emit` 5.3%,
  `html_template_plan` 5.0%, `scan_readable` 3.5%, `string_join_parts_write`
  1.5%, `sqlite3VdbeExec` 1.4%, `string_join_write` 1.4%, `publish` 1.4%,
  `al_stdlib_arguments` 1.3%, `read_u64_le` 1.3%, `clear_page_erms` 1.2%.
- By Flow word: `rails-message` 17.8%, `page-query` 11.7%, `message-row`
  7.2%, `cached-room-page` 6.9%, `csrf.mask` 5.9%, `avatar-token` 4.9%,
  `message-page` 4.8% (`list.append`, item 3), `csrf.digest` 3.6%,
  `csrf.pad-at` 2.2%, `message-fragment` 2.0%, `show-room` 2.0%,
  `http-response` 1.6%, `flow_http_handle` 1.5% (`publish`), `cache.key`
  1.2%, `db.column` 1.1% (session row).

**After 281, cached:**
- By Flow word: `page-query` 16.4%, `message-fragment` 12.6%, `message-row`
  10.0%, `cached-room-page` 9.4%, `message-page` 7.1%, `avatar-token` 6.1%,
  `show-room` 2.8%, `http-response` 2.2%, `flow_http_handle` 1.9%.
- Flat: `scan_value` 11.7%, `utf8_valid` 8.6%, `copy_range` 7.4%,
  `validate_type_descriptor` 6.5%, `scan_text` 5.9%.

The 8.6% `utf8_valid` is in the cache-hit path:
`al_owning_commit_host_output` -> `measure_external_value` re-validates every
`cache.get` hit. That String was already validated when `cache.put` stored it
from verified arena bytes. Skipping the check needs a per-operation trusted
output flag in the host-op emission and in `commit_host_output`. `db.query`
text goes through the same boundary and must keep its validation.

**Other costs (not the host's):**
- `html_template_write` and `_plan` (~12%) and the stdlib emit/plan path
  (items 2 and 4).
- HMAC through `EVP_Q_mac`, plus `EVP_MAC_fetch` and its rwlocks (~4.5%,
  item 6).
- The arena copies of the 460 KB page: `show-room`, `http-response` and
  `publish` together are ~5%.

**What changed in the host (281):**
- Lazy buffer reservations.
- Response bodies of 16 KB or more go out with one gather send straight from
  the response buffer. This removed the 1.7% memmove into the connection
  buffer.
- The context is borrowed instead of copied.
- Per-worker peaks are logged at shutdown.

HTTP parsing, epoll, SQLite binding and the cache copy were each under 1% and
were left as they are.

**Smoke results, uncached and cached.** Each figure is a median with its
range, over 3 interleaved runs per configuration of the harness's flow type.
The runs used the harness copy with `PERF=0`, which is identical to
`native-lifetime.sh` apart from where it writes its output:

| build | cache | req/s | CPU ms/req | p50 / p99 ms | PSS MB |
| --- | --- | --- | --- | --- | --- |
| before (89b3923) | off | 972 [909-975] | 4.22 [4.13-4.23] | 13.2 / 42.8 | 171.3 |
| 281 | off | 1517 [1409-1572] | 2.61 [2.58-2.72] | 7.4 / 20.9 | 20.0 |
| before (89b3923) | 32 MB | 1146 [1111-1167] | 3.50 [3.32-3.57] | 8.9 / 42.1 | 171.7 |
| 281 | 32 MB | 1802 [1778-1859] | 2.02 [1.99-2.07] | 6.4 / 14.9 | 19.8 |

All runs returned 0 invalid responses. The 276 figures were 1126 req/s,
3.62 ms and 171 MB; this session's "before" runs were slower than that on the
same binary.

**Memory, before.** PSS was 171.8 MB, almost all private anonymous memory:
- four glibc thread heaps of about 33.5 MB each, the 32 MB arenas;
- one 37 MB mapping holding 4 x (5.1 MB input + 4 MB response buffer).

All of it was resident because `fh_alloc` was `posix_memalign` plus
`memset`.

**Memory, after.** PSS is 20 MB: 16.9 MB anonymous and 3.4 MB file-backed
(libcrypto, SQLite, the binary). The anonymous part is:
- the worker buffers, 7.4 MB resident of 86 MB reserved (touched arena
  pages, the 460 KB response, the input);
- the glibc heaps, about 7-8 MB (SQLite page caches and statements,
  connection buffers);
- the cache table, under 1 MB for 40 fragments.

**Measured peaks, per worker:**
- Arena cursor: 17.3-17.6 MB. This includes the 16 MB output reservation of a
  host call such as `db.query`, which is reserved but mostly not touched, so
  keep `--arena-mb 32`.
- Response: 460.5 KB.
- Input: 824 B with the context borrowed, 1064 B before.
- Context: 240 B.
