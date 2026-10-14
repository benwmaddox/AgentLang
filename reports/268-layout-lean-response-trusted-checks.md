# 268 — Rails layout in Flow, lean response, trusted-profile check removal

Status: Campfire app milestone (views plan step 3, slice 1) plus a backend
performance fix. The room page now renders inside the Rails application layout,
written in Flow. The response no longer carries message rows. A cost probe of
the full Rails message partial found two runtime costs that grew with template
size, and both are removed in the trusted profile.

## User direction

- "We need apples to apples to really compare."
- Run the quick benchmark of AgentLang only, against the recorded Rails and Rust
  runs; never rerun Rails or Rust.
- "Why is the whole stack ever getting checked at runtime? Isn't that all
  information known at compile time?" → "Do that cleaner fix."

## Rails layout in Flow

- **`applications/campfire/layout.flow` (new).** `campfire.views.page(chrome:
  PageChrome, messages: List<Html>) -> Html` is Campfire's open-source layout and
  room chrome (`layouts/application`, `rooms/show`), about 33 KB, written as one
  `html"""…"""` template.
  - It is generated once from the private Rails capture. Every request, user,
    room and account value is a named hole on `PageChrome`, and the capture
    itself is never committed.
  - It has 35 chrome holes plus the message list.
- **`views.flow`:**
  - `campfire.views.room-chrome(room, user, base-url)` derives the room kind,
    `rooms_<kind>` key, path and body class (`sidebar admin` for
    administrators).
  - `campfire.views.room-page` wraps the room's messages in the layout.
  - The show-room route in `http.flow` uses it.
- **Html rule change:** a String hole is now allowed inside `<script>`/`<style>`
  raw text. It is escaped like any String hole. An Html hole there is still
  rejected. Docs and the template tests are updated.
- **Named gaps, empty for now:** CSRF and composer tokens, signed avatar URL,
  signed stream name, account logo version, room loaded-at.

## Lean response

- `messages` is removed from `CampfireHttpResponse`. The rows are rendered into
  `body`, so carrying them was only extra retained-output copying.
- The F# and C renderers drop the messages parse and consistency check. The
  renderer pin is re-pinned.
- The room-read verifier now counts ` data-message-id="` in the body and checks
  the Unicode case with `body.Contains`.

## Cost probe: a Rails-sized message partial

A synthetic partial was generated from one captured Rails message: 11,627
template bytes, 58 holes on an 11-field `BenchMessage` record. The probe and its
capture are private and not committed; the harness takes it with
`--profile --probe <file>`. It renders 40 of these, about 465 KB, in the trusted
O2 runtime. Native output is checked against the interpreter before timing.

| Step | 40 × partial (median) | Per hole |
| --- | ---: | ---: |
| Start | 1,521 µs | 0.54 µs |
| Trusted `locate_field` walks only to the target | 983 µs | 0.31 µs |
| Per-instruction stack check is diagnostic-only | **681 µs** | **0.17 µs** |
| Same markup with no holes (floor) | 276 µs | — |

Two costs were fixed, and both grew with template size:

1. **`al_owning_locate_field`** revalidated the layout's type graph, using 8 KB
   of memo with `__chkstk`, and scanned the whole parent record on every field
   read. In the trusted profile it now walks only the fields before the target,
   with bounds still checked. The diagnostic profile keeps the full scan.
2. **Whole-stack check after every instruction.** The backend re-checked every
   live stack descriptor (range below the cursor, init bits) after each
   instruction. With 58 holes on the stack that was about 3,400 runtime calls
   per partial, quadratic in stack depth.
   - These checks verify the compiler's own runtime bookkeeping, not the program,
     whose shapes and types are proven at compile time.
   - Generated IR now depends on the profile. `Diagnostic` keeps the full check
     as a codegen self-test. `TrustedGenerated` emits none and keeps the
     boundary checks (arguments, host input, arena reservation) and the per-use
     descriptor checks.
   - Recorded in `docs/PENDING-REVIEW.md`.

**Guards added:**
- The views gate now also runs the trusted O2 build against the oracle on every
  case.
- Every profile timing first checks native output against the interpreter.

**Still open:**
- `al_owning_html_template_write` re-runs the plan, so each hole is scanned
  twice.
- No-op layout event calls remain in trusted IR.
- About 0.17 µs per hole remains.

At 681 µs for a full Rails-sized message list, rendering fits well within the
recorded Rust run's 2.93 ms of CPU per request.

## Performance (quick benchmark)

| Server | Page bytes | Req/s | CPU / req | p99 | Peak working set | Peak private |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| **AgentLang 268 (layout, lean, trusted checks)** | 42,813 | **9,123** | **0.42 ms** | 2.17 ms | 12.6 MB | 46.2 MB |
| AgentLang 268 (layout + lean only) | 42,813 | 7,269 | 0.54 ms | 2.69 ms | 12.8 MB | 46.3 MB |
| AgentLang 268 (layout only) | 42,813 | 7,078 | 0.54 ms | — | — | — |
| AgentLang 267 | 10,041 | 8,099 | 0.48 ms | 2.5 ms | 12.4 MB | 46.3 MB |
| Rust, identity (recorded) | 416,127 | 1,322 | 2.93 ms | 25.9 ms | — | — |
| Rails, identity (recorded) | 463,753 | 19 | 176 ms | 1,108 ms | — | — |

- **Layout:** the page grew 4.3× (10 KB → 43 KB) and cost −13% req/s.
- **Check removal:** recovered that and more, +25.5% req/s and −22% CPU per
  request against the lean build.
- **Not equivalent:** the served page is byte-identical before and after the
  check removal, but it is still far from the Rails page. No speed ratio is
  claimed.

## Parity

`compare_html_structure.py --mask-deployment` against the Rails capture:
- **AgentLang 268:** 4,580 of 4,896 Rails nodes missing (267: 4,894).
  - All layout chrome matches except the named gaps.
  - The remaining nodes are inside the 40 messages: Rails renders about 115 nodes
    per message, and this page renders about 3.
- **Rust port, for comparison:** 325 missing, all CSRF.

## Tests

- **`campfire-views`:** the trusted O2 build now matches the oracle on every case.
- **Room-read verifier:** 238 assertions, in both the trusted and the diagnostic
  profile.
- **Gates:** all 21 pass (run `gates-20261014T021428Z`, about 30 min), on frozen
  sources.

| Gate | Result | Seconds |
| --- | --- | ---: |
| llvm-full-suite | pass | 295 |
| arena-lifetime | pass | 0 |
| owning-compact | pass | 10 |
| owning-html | pass | 40 |
| owning-listbuild | pass | 23 |
| owning-utf8 | pass | 9 |
| owning-templates | pass | 16 |
| campfire-views | pass | 10 |
| acceptance | pass | 9 |
| flow-tests | pass | 7 |
| flow-runtime-tests | pass | 30 |
| ir-interpreter | pass | 4 |
| campfire | pass | 277 |
| native-value-stack | pass | 312 |
| owning-mailbox | pass | 68 |
| ir-parity | pass | 12 |
| native-conformance | pass | 284 |
| owning-lists | pass | 11 |
| owning-list-ops | pass | 20 |
| room-read-trusted-off | pass | 170 |
| room-read-diagnostic-off | pass | 178 |

## Next

Queued for 269, approved by the user. Run the probe and the quick bench after
each step.

1. **Stage-profiled host build.** Measure the current per-request split between
   SQL, the host adapter and Flow/runtime, replacing the report-256 numbers.
2. **Trusted IR cleanup:**
   - stop emitting the no-op `al_owning_record_layout` trace events (194 calls
     in the probe IR);
   - have `al_owning_html_template_write` take the plan's results instead of
     re-planning;
   - drop the `ctx->status` checks after runtime calls that cannot fail.
3. **Static or inline field location.** Use a constant offset when every
   preceding field is fixed-size; otherwise emit an inline header walk. This
   removes every `al_owning_locate_field` call.
4. **Only if the profile still shows call overhead:** compile the runtime to
   bitcode and link it into the module so LLVM can inline its small helpers.

Kept: the boundary checks (arguments, host output commit, reservation, depth
guards) and the full diagnostic profile.

Then views plan, slice 2 onward:
1. Host data for messages with their creators (one coarse operation, new tag),
   then avatars, permalinks, timestamps and day separators.
2. Boost forms with per-form masked CSRF tokens; boosts.
3. Rich text sanitized to Rails' allow list, then `html.raw`.
4. Head, sidebar and composer gaps.
5. Rerun the report-259 protocol.
