# Per-worker store and in-order response output (plan of record, 2026-10-15)

User direction (2026-10-15):
- Caching is per worker, for isolation. No memory is shared between workers.
- Each worker owns a fixed static section. Its size is configurable as a
  **total for the process, split evenly between workers**. For the Campfire
  benchmark the total is 32 MB, so 8 MB each with 4 workers. The section never
  grows.
- The cache logic lives in Flow: the ring, the index and eviction. Only small
  primitives are native.
- Eviction is oldest-first, not LRU. Entries are packed without page waste.
  16 KB pages are only the unit for reporting fill.
- A cached value can go to one of two places, chosen by the app per call:
  - **output:** copied straight into the response buffer;
  - **data stack:** copied into the request arena as a value.

This is candidate 4 of [STACK-ONLY-RESEARCH.md](STACK-ONLY-RESEARCH.md):
declared retained data outside the request arenas, reached through declared
effects, never through mutable globals. Data enters it only by checked copies,
and nothing in it points into request scratch. Capacity is chosen once at
startup.

The report 107 re-entry model, where state is passed in and returned and
copied on every turn (`ExecuteRetainedWithInputs`), is **rejected** for worker
state: it copies the whole state per turn.

It replaces the process-wide lock-free cache of report 277 for Campfire, which
used `cache.get`/`cache.put` and `flow_http_cache.c`. With no cross-worker
state, the host has no shared mutable memory left.

## 1. The worker store (native, small)

- **Allocation:** one region per worker, `--store-mb N` total (default 0 = no
  store), each worker getting `floor(N MB / workers)` rounded down to 16 KB.
  The region is a lazy reservation (`mmap` `MAP_NORESERVE` on Linux, reserved
  on first touch on Windows), so only the pages written become resident. It is
  zero-filled.
- **Primitives:** stdlib host operations in `src/stdlib/store.flow`, under the
  effects `store.read` / `store.write`. Offsets are byte offsets into the
  worker's region, and every access is bounds-checked. An Int access must be
  8-byte aligned; a failed check gives none or false, never a crash.
  - `store.capacity() -> Int`: the region size in bytes, 0 when there is no store.
  - `store.int(offset: Int) -> Option<Int>`: read 8 bytes as an Int.
  - `store.set-int(offset: Int, value: Int) -> Bool`
  - `store.write(offset: Int, text: String) -> Bool`: copies the String's bytes.
  - `store.bytes(offset: Int, length: Int) -> Option<Bytes>`: data-stack read,
    a copy into the arena. Bytes need no UTF-8 check.
  - `store.text(offset: Int, length: Int) -> Option<String>`: data-stack read
    as a String. It is UTF-8 validated, because the region holds raw bytes that
    Flow logic wrote and the runtime cannot prove them valid.
  - `store.hash(text: String) -> Int`: a 64-bit FNV-1a hash of the bytes, so
    Flow can index keys without a byte loop.
  - `store.matches(offset: Int, text: String) -> Bool`: an in-place compare,
    so checking a key copies and validates nothing.
  - `store.ints(offset: Int, count: Int) -> Option<List<Int>>`: reads one
    index slot or one entry header in a single call.
- **Measure first:** before the cache is written, a trusted-O2 micro-benchmark
  of a `store.int` loop sets the per-call cost. With up to about 8 calls per
  lookup and 40 lookups per page, the cache must stay well under the C cache
  it replaces. If host calls are too dear, the coarse reads above carry the
  probe.
- **Interpreter:** one zeroed byte array per worker, with the same semantics
  and bounds. Attached tests get a fresh store of a size the test sets.
- **Failure model:** a request that fails part-way may leave some writes in
  the store. The cache layout below is designed so that any partial write
  reads as a miss, never as a wrong value.

## 2. The response writer (native, small)

- **Primitives:** host operations in `src/stdlib/http.flow`, under the effect
  `response.write`:
  - `response.write(html: Html) -> Bool` appends the bytes to the worker's
    response body buffer.
  - `response.write-store(offset: Int, length: Int) -> Bool` appends a store
    range straight to that buffer: one copy, no arena copy, no validation.
- **Opt-in body assembly:** streamed bytes are used only when the response asks
  for them. `HttpResponse` gains `streamed: Bool`:
  - when it is true, the body is everything written followed by
    `HttpResponse.body`;
  - when it is false (an error, a redirect, a bare 500 from a failed contract
    check), the written bytes are discarded.

  The host sets `Content-Length` from the total, and HEAD sends no body either
  way. `response.discard()` also drops what was written so far. Existing apps
  that never write and never set `streamed` are unchanged.
- **Tests:** `http.streamed-body(response) -> String`, interpreter only, gives
  attached tests the assembled body (written bytes plus `body`), so tests
  assert on the whole page.
- **Copy timing:** each write copies immediately. A later store write or
  eviction in the same request cannot affect bytes already written, so no
  entry needs pinning.
- **Interpreter:** a per-request buffer with the same appends.

## 3. The cache, in Flow (`src/stdlib/worker-cache.flow`)

All of the following lives inside the worker store.

- **Header**, fixed at offset 0:
  - `head`, the next write offset in the ring;
  - `tail`, the oldest live entry;
  - `wrap`, the end of the written span before the last wrap, so `tail` knows
    where to jump to the ring start;
  - `live-bytes` and `live-entries`, so `head == tail` is never ambiguous
    between an empty and a full ring;
  - `seq`, the next sequence number;
  - the index slot count `S` (a power of two);
  - the ring start and end;
  - counters: hits, misses, puts, evictions and bytes in use.
- **Index**: `S` slots of 24 bytes each, holding the key hash, the entry
  offset and the entry sequence number. Lookup is linear probing over a
  bounded window (8 slots).
- **Ring:** entries are packed and 16-byte aligned. Each entry has a header
  (sequence number, key length, value length, total length) followed by the
  key bytes and the value bytes.
- **Lookup `worker-cache.find(key) -> Option<CacheEntry>`:**
  - hash the key and probe the index;
  - for a candidate slot, check that the entry header at its offset carries
    the same sequence number and key length, and that the key bytes match;
  - a slot pointing at overwritten space fails the sequence check and is a
    miss. Eviction never has to clean the index.
  - `CacheEntry` is (value offset, value length). It stays valid until the
    next `put` in the same request.
- **Reads:**
  - `worker-cache.write-to-response(key) -> Bool` (output path) is
    find + `response.write-store`.
  - `worker-cache.text(key) -> Option<String>` and
    `worker-cache.bytes(key) -> Option<Bytes>` (data-stack path) are find +
    `store.text` / `store.bytes`.
- **`worker-cache.put(key, value) -> Bool`, oldest-first:**
  - The entry needs `n = align16(header + key + value)` bytes. An entry larger
    than a quarter of the ring is refused (false).
  - If `head + n` passes the ring's end, record `wrap = head` and continue at
    the ring start. The space from `wrap` to the ring's end is a dead gap:
    when `tail` reaches `wrap` it jumps to the ring start.
  - While the live span from `tail` to `head` would overlap the new entry,
    evict the entry at `tail`: advance `tail` past it and count an eviction.
    Each put evicts as many oldest entries as it needs.
  - **Write order, so a partial write is never visible:** zero the header's
    sequence number, write the key and value, write the header, then write the
    sequence number last. Only then advance `head` and the live counts. Then
    claim an index slot: a free one, else one whose entry is stale, else the
    oldest in the probe window.
- **Fill reporting:** `worker-cache.stats()` returns entries, bytes in use,
  capacity, and 16 KB pages touched and in use. The host logs them per worker
  at shutdown.
- **Keys** are app strings, as today: template version, record id plus
  `updated_at`, and the variant. Old versions are never looked up again and
  age out of the ring.

## 4. Campfire on the store

- **Write the page in order:**
  - `response.write(layout head)`;
  - for each message, either `worker-cache.write-to-response(key)`, or render
    the fragment, `response.write` it and `worker-cache.put` it;
  - `response.write(layout tail)`.

  The layout splits into a head and a tail template around the message list,
  which removes the placeholder splice in `campfire.server.cached-room-page`.
- **Do not regress the uncached column.** With the store off
  (`store.capacity() == 0`), the room page keeps today's single joined query.
  The narrow-then-load path below runs only with a store. Its misses are loaded
  in one batched query (a JSON list of ids through `json_each`), not one query
  per message. Acceptance: uncached CPU per request is no worse than the 283
  build's.
- **Fetch only what the frame needs first, like the Rust reference**
  (`Message::last_page`, then `presenter.message()` only on a miss):
  - the first query selects only id, client message id, creator id and
    `updated_at` for the 40-message page;
  - creators, bodies and avatar tokens are loaded and signed only for messages
    whose fragment misses.
- `--cache-mb` and `flow_http_cache.c` stay in the tree for now, unused by
  Campfire. They are deleted when nothing else uses them.
- **Scope:** only the portable Flow server (`applications/campfire/server/`)
  uses the store. The HTTP.sys and .NET hosts have no store operations; their
  shared room path is unchanged.
- `store.read`, `store.write` and `response.write` join the closed effect
  vocabulary.
- Splitting the layout must leave `campfire.views.page` byte-identical; a test
  checks that the page equals head ++ fragments ++ tail.

## 5. Tests and measurement

- **Store primitives:** bounds, alignment, a zero-capacity store and data-stack
  reads. Native results must equal the interpreter's at O0, O2 and trusted O2.
- **Worker cache (Flow tests):**
  - hit and miss, and overwrite through wrap-around;
  - eviction of several entries for one large put;
  - a stale index slot reads as a miss;
  - a partial write (simulated with an override) reads as a miss;
  - the over-quarter refusal;
  - stats.
- **Harness:** the parity script's `-CacheMb` and the sweep's `FLOW_CACHE_MB`
  become `--store-mb`. Each lifetime records the per-worker store size in
  `cache-env.txt`, and a store-off run must show zero store traffic in the
  shutdown stats.
- **Campfire parity:**
  - with the store on and off, the page is byte-identical to the uncached
    native host;
  - the first and the steady response are identical;
  - for a page of hits, the trace shows only the narrow query.
- **Benchmark (fair Linux harness):** Flow with `--store-mb 32` (8 MB per
  worker) against Rust with `CAMPFIRE_FRAGMENT_CACHE_MB=32` (one shared store,
  response cache off), cached and uncached, interleaved in one session, with
  saturation labels. Report the per-worker split, and the resident memory next
  to the reserved memory.
- **Target:** cached Flow CPU per request at or below Rust's in the same
  session.
