# 269 — Current request profile; trusted IR cleanup; inline field location

Status: backend performance milestone (the queue approved after 268). It
re-measures where a native room GET spends its time, then makes three cuts that
apply only in trusted builds. The Campfire app is unchanged.

## User direction

- "And for the aot build we are benchmarking, is there anything else we should
  pull out of the IL to make things more efficient?" → "Queue those changes for
  after 268 gate."
- "How much time is the worker waiting for sql vs doing other stuff? What did
  rails and rust do while waiting?"

## Where a room GET spends its time (stage-profiled host)

Setup: the report-256 protocol (O2 trusted host, stage timing on in both arms,
execution `Off`/`Detailed`/`Detailed`/`Off`, 4 workers on the pinned 4-core
mask, 16 clients, two 15-second repeats). Runs `p269b-*`.
- The 256 client pins the response body hash, so the private template was
  re-pinned to the 268 page (38,874 bytes, status 200), fetched once from a
  disposable host lifetime.
- The aggregator validated all 4 runs.

| Run | Correct req/s | Server cores | p99 upper bound |
| --- | ---: | ---: | ---: |
| Off A | 15,103 | 3.77 | 1.75 ms |
| Detailed B | 9,556 | 3.80 | 2.43 ms |
| Detailed C | 9,571 | 3.83 | 2.46 ms |
| Off D | 14,826 | 3.82 | 1.58 ms |

Per GET, from the Off arms (about 253 µs of CPU):

| Stage | µs | Share |
| --- | ---: | ---: |
| Language execution (`api_execute`) | ~198 | ~76% |
| ↳ SQLite step, reset and bind (89 / 81 / 9 calls) | ~80–90 | ~33% |
| ↳ Host adapter outside SQL (row encoding, output commit, cookie HMAC) | ~20 | ~8% |
| ↳ Flow and runtime, including the 38 KB layout render | ~90–110 | ~38% |
| HTTP.sys send | ~44 | ~17% |
| Input preparation, response rendering, scrub | ~20 | ~7% |

- The SQL figures come from the Detailed arms, whose counters inflate every
  interval; treat them as ±15 µs.
- SQLite time is CPU time on a warm page cache. It is not I/O wait; nothing
  in the read path waits.
- Report 256 had Flow and runtime at about 265 µs of a 518 µs Detailed GET.
  It is now about the size of SQLite, on a page 4× larger.

**How the references handle waiting.** Rust (tokio, axum, rusqlite) runs a
read on the request's own thread when a reader connection is free, and offloads
it to a fixed set of reader threads otherwise. Its own study found that hops
cost more than warm-cache reads. Rails (Puma, 1 process × 5 threads) overlaps
I/O through the GVL, but it spends about 176 ms of CPU per request rendering,
so database waits are negligible. Neither oversubscribes threads for reads.
The native host's shared HTTP.sys queue, with one worker per core, matches
that.

## Trusted IR cleanup

All three changes apply to the `TrustedGenerated` profile only. The
diagnostic profile keeps every check.

1. **No layout trace events.** Eleven `al_owning_record_layout` emission sites
   now go through `emitTraceEvent`. The trusted runtime's event was already
   empty; this removes the call and the status check after it.
2. **Each template is planned once.** `al_owning_html_template_write` now takes
   the plan's string and payload sizes. The trusted runtime uses them directly.
   The diagnostic runtime re-plans and fails if the numbers differ.
3. **Inline field location.** When every field up to the target is fixed-size
   (zero-width included) or a `String`, the trusted IR computes the offset from
   the static layout:
   - fixed-size fields add a constant;
   - `String` fields add their padded length header;
   - one bounds check confirms the field ends inside its parent.

   Any other dynamic field still calls `al_owning_locate_field`, so
   `MessageWithCreator` will put its fixed-size fields first.

Rails-sized partial × 40 (about 465 KB), trusted O2, median. The native output
is checked against the interpreter before each timing.

| Step | µs | Per hole |
| --- | ---: | ---: |
| 268 end | 681 | 0.17 µs |
| + no trace events, single plan | 581 | 0.13 µs |
| + inline field location | **446** | **0.07 µs** |
| No-hole floor (same markup) | 274 | — |

The room view (40 messages) went from 87 to 76 µs.

## Performance (quick benchmark)

| Server | Page bytes | Req/s | CPU / req | p99 | Peak working set | Peak private |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| **AgentLang 269** | 42,813 | **9,344** | **0.41 ms** | 2.12 ms | 12.4 MB | 46.2 MB |
| AgentLang 268 | 42,813 | 9,123 | 0.42 ms | 2.17 ms | 12.6 MB | 46.2 MB |
| Rust, identity (recorded) | 416,127 | 1,322 | 2.93 ms | 25.9 ms | — | — |
| Rails, identity (recorded) | 463,753 | 19 | 176 ms | 1,108 ms | — | — |

- The quick bench moves only +2.4%, because today's message markup has about
  3 holes per message. The per-hole savings will show up once the full Rails
  partial lands (slice 2).
- The pages are not equivalent, so no speed ratio is claimed.

## Tests

- `campfire-views` and the template suite: trusted output equals the oracle and the interpreter.
- Room-read verifier: 238 assertions in both the trusted and the diagnostic profile.
- Gates: all 21 pass (run `gates-20261014T030637Z`, about 31 min), on frozen sources.

| Gate | Result | Seconds |
| --- | --- | ---: |
| llvm-full-suite | pass | 303 |
| arena-lifetime | pass | 0 |
| owning-compact | pass | 10 |
| owning-html | pass | 41 |
| owning-listbuild | pass | 24 |
| owning-utf8 | pass | 9 |
| owning-templates | pass | 15 |
| campfire-views | pass | 11 |
| acceptance | pass | 14 |
| flow-tests | pass | 7 |
| flow-runtime-tests | pass | 36 |
| ir-interpreter | pass | 5 |
| campfire | pass | 318 |
| native-value-stack | pass | 319 |
| owning-mailbox | pass | 69 |
| ir-parity | pass | 13 |
| native-conformance | pass | 286 |
| owning-lists | pass | 11 |
| owning-list-ops | pass | 20 |
| room-read-trusted-off | pass | 171 |
| room-read-diagnostic-off | pass | 175 |

## Next

1. **270a**, ready as a patch and recommended by a read-only survey. In
   trusted builds:
   - skip `check_initialized` in descriptor checks (about 4,400 calls per GET;
     the inline range check already covers it);
   - stop re-walking the static layout's type graph on every measure, locate
     and compare.
2. **Slice 2: full message partial.** New host operation tag 10:
   - It returns messages with their creators' name, bio and `updated_at`.
   - Timestamps come back as stored, plus epoch milliseconds from
     `unixepoch()` (exact here, because every value is whole seconds).
   - The adapter signs one avatar token per creator. The derivation matches all
     4 distinct tokens in the Rails capture, checked by match count only.
   - Flow builds the ISO times, avatar stamp, title, permalinks and the full
     partial, using the new `string.replace` primitive (built and tested on a
     worktree branch).
   - Every Rails message carries its own day separator, so no previous-message
     logic is needed.
3. Then: CSRF-tokened forms (slice 3), boosts, rich text, and the report-259
   protocol rerun.
