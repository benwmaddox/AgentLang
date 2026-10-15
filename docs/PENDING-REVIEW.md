# Pending review

Design topics the user has asked to revisit later. Each entry says what was
decided for now and why it stays open. Add new items rather than deciding them
silently.

| Topic | Current decision | Revisit because | Since |
| --- | --- | --- | --- |
| Immutable strings | Strings are immutable and bump-allocated on the request data stack. Building a page copies pieces into joins. | Copy volume for large pages. Alternatives: last-use in-place append, or part lists written straight to the socket ([RESPONSE-STREAMING.md](RESPONSE-STREAMING.md)). | 2026-10-13 |
| Workflow ceremony | A word must have a passing attached test before commit, and Flow code goes through define, commit and test. | Feels heavy when iterating on UI templates. Kept for now; review against agent authoring experience. | 2026-10-13 |
| Per-call native overhead | Report 266 removed an 8 KB memo clear from every value measure. A 40-message room render still costs about 125 µs: field location through record scans, map callback frames, and copying list results. | Measure these per element and compare them with Rails and Rust before a full page lands. Map results are copied into the list and then into the page (three writes per byte). | 2026-10-13 |
| Shared response cache | Not implemented. Caching is disabled in all benchmark servers. | A cross-worker cache is a new cross-thread dependency. Discuss it before adopting ([RESPONSE-STREAMING.md](RESPONSE-STREAMING.md#cached-parts)). | 2026-10-13 |

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
3. `inline` attribute on `fn` (user approved, 2026-10-14): a hint that the
   backend emits a small word's body at the call site instead of a native call
   (`campfire.ua.byte`, `digit?` and similar helpers).
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

1. **Profile the Linux portable path first** (`perf` under load). Moved from
   C into Flow at once: cookie decrypt + JSON, generic `db.query` rows with
   loop-based accessors, worker context copied into every request. Also
   compare measurement scopes (cgroup on Linux vs process CPU on Windows).
2. **`html.raw` re-validates already-validated markup** (cache agent's probe:
   costs about as much as rendering). Trusted inputs should skip it.
3. **`list.append` in loops is O(n^2)** (same probe, similar size). Needs an
   amortized append / builder for loop accumulation.
4. **Stdlib call overhead** ~70 ns per small byte/string op (plan + write
   passes, argument scans), paid >1,000 times per page (CSRF alone ~0.2 ms).
   Leaner small-op path or fused common sequences.
5. **Word calls pass arguments through the arena** (~23 ns/call after
   scalars-in-registers). Register calling for scalars, and the approved
   `inline` attribute.
6. **OpenSSL per-call setup** ~0.8 us x ~41 HMACs/page: per-worker cached
   digest/HMAC objects (user approved; thread-local, no sharing).
7. **`Option` results through the arena**: keep small Options in registers
   when matched immediately; then decide whether `string.byte-at-or` stays.
8. **Memory**: Flow PSS 171 MB vs Rust 85 MB; likely generous per-worker
   arena and buffer reservations. Measure and right-size.
9. **Worker context copied per request** (276 deviation): borrow it from a
   protected region below the arena mark instead.
10. **Missing native `list.get` / `list.count`** (forces loop helpers).

Measured reference points: sum loop 0.52 ns/iteration; small-word call loop
23 ns/iteration; lean User-Agent 29-31 us; HMAC-SHA256 0.61 us (Windows,
BCrypt) / 1.22 us (Linux, OpenSSL one-shot); stdlib ops: concat/encode ~72 ns,
slice/xor ~125 ns, mask one CSRF token ~247 ns.
