# 284 — Per-worker store, worker cache in Flow, in-order response stream

User design (2026-10-15), plan of record [docs/WORKER-STORE-PLAN.md](../docs/WORKER-STORE-PLAN.md):
- Caching is per worker, for isolation: no memory is shared between workers.
- Each worker owns a fixed static section. `--store-mb` is a total split
  evenly between workers; for the benchmark it is 32 MB, so 8 MB each.
- The cache logic is written in Flow, with oldest-first eviction over a packed
  ring and no page waste.
- The app chooses per call whether a cached value goes to the output buffer
  or onto the data stack.

## What was built

| Piece | Summary |
| --- | --- |
| Effects | `store.read`, `store.write` and `response.write` join the closed vocabulary. |
| Store primitives | Host operations (tags 5-17; `src/AgentLang.FlowHttp/Store.fs` holds the table for both interpreter and native): `store.capacity/int/ints/set-int/write/bytes/text/matches/hash`. Offsets are bounds-checked and Int accesses 8-byte aligned. A failed check gives none or false. |
| Response stream | `response.write(String)`, `response.write-store(offset, length)` (store bytes straight to the output: one copy, no arena, no validation), `response.written`, `response.discard`. `HttpResponse.streamed` decides whether the stream goes before `body`. Errors and redirects drop partial writes, and HEAD keeps the streamed length. |
| Host | The per-worker store is a lazy, zero-filled reservation in whole 16 KB pages, next to a response stream of `--retained-mb`. At shutdown each worker logs `store-bytes` and `store-pages-used`. |
| `src/stdlib/worker-cache.flow` | A 128-byte header, a fixed index of (hash, offset, seq) slots, and a packed 16-byte-aligned ring. Puts evict from the tail, as many as needed, and clear the evicted entries' index slots. A put writes its seq last, so a cut-short put reads as a miss. A hit costs five host calls. |
| Campfire (`server/`) | **With a store:** a narrow query of id and `updated_at` (like Rust's `Message::last_page`); one batched `json_each` query for the misses only, so creators, bodies and avatar signing happen only on misses; the page written in order (frame head, hits from the store, rendered misses, frame tail); puts after the page is written. **Without a store:** the one-piece page from the single joined query. Campfire no longer uses the process-wide `cache.get`/`cache.put` cache. |

**Layout split.** Head and tail templates fail the compile-time markup check,
because the head leaves `<html>`, `<body>` and the messages `<div>` open.
`campfire.server.page-frame` instead renders the page with no messages (about
33 KB) and splits it right after the messages container's opening tag. A test
checks that head ++ messages ++ tail equals the page. A template form that may
leave elements open is listed in docs/PENDING-REVIEW.md, along with moving
`Html` into the stdlib (user, 2026-10-15) and zero-input host operations.

## Tests

- **Worker cache (attached tests):**
  - hit and miss to both destinations;
  - eviction across wraps, and a large put that evicts many entries;
  - the quarter-ring refusal;
  - a cut-short put reads as a miss;
  - separate caches in separate regions;
  - slot and stats checks.
- **Campfire:**
  - The streamed page is byte-identical to the one-piece page, cold and from
    the cache. The second request writes the same bytes from 2 stored
    fragments.
  - The page frame joins to the page.
  - Narrow keys, the batched miss query, in-order hit/miss writes, and
    membership are all tested.
- **FlowHttp:** 527 assertions. The counter app's store routes (streamed miss,
  put, hit from the store, data-stack read, discard, non-streamed error after
  partial writes, streamed HEAD length, a server with no store) give the same
  results in the interpreter and natively, on Windows and Linux, in the
  trusted and diagnostic profiles.
- **Native parity:** PASS on Windows and Linux, with `--store-mb 0` and 32. The
  page is byte-identical to the native HTTP.sys host; first == steady;
  `loadgen --validate` reports 0 invalid; each worker has 8 MB and uses 30 store
  pages.
- **Gates:** 25 of 25 pass on the first run (`gates-20261015T142105Z`).

## Performance: interleaved Linux sweep (same protocol as 282)

Rails, Rust and Flow, each on 4 pinned vCPUs with the load generator on 8.
3 reps × 6 configurations × 16/32/64/128 connections, order rotated, 72 cells,
0 invalid responses. Pre-registered in `PLAN.md` (in the evidence).

- **Rust "on":** fragment cache 32 MB, response cache off.
- **Flow "on":** `--store-mb 32`, split into 8 MB per worker.
- **Rails "on":** redis fragments.

The sweep waited for a quiet VM because Codex was running StasisLang tests in
the same WSL instance. No foreign process shows CPU in any lifetime's start
line.

| Peak (saturated cell, median of 3) | Uncached | Cached |
| --- | --- | --- |
| Flow | 3,488 req/s, **1.21 ms** CPU/req (64 conn), PSS 16 MB | 6,094 req/s, **0.69 ms** (32 conn), PSS 46 MB |
| Rust | 1,563 req/s, **2.53 ms** (16 conn), PSS 98 MB | 9,691 req/s, **0.43 ms** (16 conn), PSS 48 MB |
| Rails | not saturated (wait-bound): 19.6 req/s, 165 ms | not saturated (wait-bound): 168 req/s, 20.4 ms |

Full table: `aggregate-s284.md` in the evidence. Latency at 16 connections:
Flow cached p50/p99 2.2/7.2 ms, Rust cached 1.5/2.9 ms, Flow uncached
4.2/15.3 ms, Rust uncached 9.8/20.8 ms.

**What it shows:**
- **The store took Flow's cached cost from 1.22 to 0.69 ms per request.**
  1.22 ms is the 283 quick check, a different session. Flow's own cache now
  saves 43% (1.21 → 0.69), against 9% in 283.
- **Rust's cached path is still about 1.6× cheaper** (0.43 against 0.69 ms).
  What Flow still does per cached request:
  - the session check and its lookups;
  - rendering the frame (the 33 KB layout every time);
  - the narrow query;
  - 40 lookups at five host calls each;
  - writing about 410 KB into the stream.
- **Memory:** Flow's PSS with the store is 46 MB, against 16 MB without it.
  Most of that is arena pages touched by the cold first request; the user
  accepted this peak. Each worker's store holds the 40 fragments plus the index
  in 30 pages (about 480 KB).

## Fairness: the uncached column is not a fair comparison yet

Rust does more work per message than Flow (`presenters.rs`):
- it loads and parses Action Text twice (plain text, then sanitized
  presentation);
- it looks up an attachment per message;
- it queries boosts and each booster.

Flow renders bodies as escaped plain text and has no attachments or boosts.
The pages are close in size (Flow 413,825 bytes, Rust 416,127), but the work
behind them is not equal. So the uncached ratio (Flow 1.21 against Rust 2.53
ms) overstates Flow, and **no uncached speed ratio is claimed.** The cached
column is closer to fair, because neither server re-renders hit fragments.
Even there, Rust's fragments contain rich-text output that Flow does not
produce.

Rails still masks CSRF tokens per page (Rust and Flow do not), and runs 3 puma
workers, so it is wait-bound at about 0.82 of 4 vCPUs.

## Next: equal pages (user decision, 2026-10-15)

**Acceptance:** for the same database, Flow's room page is byte-identical to
the Rust reference (and to Rails, apart from its CSRF tokens). SQL call counts
need not match. This needs:
1. Action Text rendering in Flow, with the same sanitizing and output;
2. attachments and their markup;
3. boosts and boosters.

The sweep is then rerun.
