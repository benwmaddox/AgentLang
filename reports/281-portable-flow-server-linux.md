# 273–281 — CSRF in Flow, OS crypto, the portable Flow server, Campfire on Linux, the lock-free cache, performance

Reports 273 to 281 cover one goal (user, 2026-10-14): benchmark Rails, Rust and
Flow Campfire as closely as possible, all three natively on Linux (WSL2), with
and without cache, on equal pinned vCPUs. That needed a Linux build of our
server. It was built as the end state of the stdlib restructure, not as a port
of the Windows-only host: a generic language HTTP host, with Campfire's
request handling written in Flow. Plans of record: docs/LINUX-SERVER-PLAN.md
and docs/STDLIB-PLAN.md. The measured performance work is listed item by item
in docs/PENDING-REVIEW.md.

## What was built

| Step | Summary |
| --- | --- |
| Stdlib step 2 | Rails CSRF tokens in Flow (`csrf.flow`). Host operations 11-13 retired (tags never reused); their C and .NET code deleted. |
| 273 | Scalars in registers: Int/Bool/Float/Unit stack entries are i64 SSA values, scalar `var`s live in allocas, joins use phi. The app-host entry admits stdlib effects (`random.read` for CSRF). OS random source is portable (`getrandom` on Linux). |
| 274 | Portable Flow HTTP host (BSD sockets: epoll + `SO_REUSEPORT` per worker on Linux, WSAPoll on Windows), an explicit owning target (Windows x64 unchanged, Linux x64), multi-entry host modules (`flow_http_handle`, `flow_http_worker_init`), stdlib `http.flow` and `db.flow` with `db.query`/`db.execute` host operations (SQLite; one prepared statement per literal per worker). Stdlib slice 2: AES-256-GCM, HMAC-SHA1, strict JSON (`Json` opaque type), Rack cookies, time formatting. |
| 275 | Every crypto primitive comes from the OS library: BCrypt on Windows, libcrypto on Linux. The portable C SHA-256/HMAC/PBKDF2 is deleted. |
| 276 | Campfire's room GET on the portable host, in Flow (`applications/campfire/server/*.flow`): worker-init key derivation, Rails signed and encrypted cookies, the room-read SQL and transactions, request checks, the response. Parity with the native HTTP.sys host on Windows and Linux. |
| 277 | A process-wide lock-free fragment cache (`cache.get`/`cache.put`, effects `cache.read`/`cache.write`), `--cache-mb` (0 disables). Campfire message fragments mirror Rails' `cache [message, "presentation-v3"]`. |
| 278 | `list.append` accumulators in loops (O(n²) to linear), `html.raw` skips the check for provably valid Html, native `list.get`/`list.count`. |
| 279 | One `al_owning_stdlib_call` per stdlib op (single validation, Ints by value, formula sizing); per-worker OpenSSL digest and MAC contexts. |
| 280 | Register calling convention for internal words, inline frame enter/leave in trusted builds, `Option<scalar>` in registers, `inline fn`. |
| 281 | Linux `perf` profile of the Campfire server; one-pass message-row decoding; worker context borrowed in place; lazy buffer reservations (PSS 171 → 20 MB); large responses sent straight from the response buffer. |

### Not ported

Sign-in (`campfire.users.authenticate-password` needs bcrypt, which neither the
stdlib nor BCrypt/libcrypto provides), the login rate limiter and
`sessions.start`. Only GET/HEAD `/rooms/<id>` is routed, as in the native host.
The Flow server therefore runs on a snapshot database and its Rails session
cookie, not on its own login. The Windows HTTP.sys Campfire host and the .NET
hosts are not deleted yet (stdlib plan step 5).

App code, 272 → 281 (non-test files under `applications/campfire`): C and
headers 11,797 → 11,476; F# 2,885 → 2,821; Flow 3,378 → 5,787. The C stays
until the Windows native host is retired.

## Language and backend

- **Registers (273, 280).** Scalars no longer pass through the arena: constants,
  scalar primitives, comparisons, loop indexes and `var`s are SSA values; word
  calls use `@word.reg` (scalars as i64, other values as their four descriptor
  fields, `Option<scalar>` as tag + payload). `@word` remains a descriptor
  adapter for validators and list callbacks. Trusted builds enter and leave
  frames inline with the same 66-frame limit and skip callee argument/result
  re-checks; diagnostic builds keep the C helpers and checks.
- **`inline fn` (280).** Parser/formatter modifier; the register body is
  `alwaysinline` at O2 when it is at most 400 emitted LLVM instructions. O0 never
  inlines. Six `campfire.ua` helpers use it.
- **List accumulators (278).** A loop-carried `var` built only by
  `v = list.append(v, x)` copies each element once onto a pending run; the latch
  moves the run next to the list.
- **Stdlib calls (279).** One runtime call per op that validates each argument
  once, takes Ints by value, sizes formula results without a plan walk, then
  reserves, writes and accounts; `bytes.length` is an inline header load.

## Performance (measured during 273–281)

Micro-measurements are trusted O2 on Windows, minimum of interleaved runs, from
the step commits and docs/PENDING-REVIEW.md. Those probes printed their timings to the
console and kept no result files, so the micro-figures are quoted, not sealed. Server figures are Linux smoke runs
(4 pinned vCPUs, 16 connections, 10 s, no `User-Agent`, every response
validated).

| Measure | Before | After | Step |
| --- | ---: | ---: | --- |
| Int sum loop, per iteration | 23 ns | 0.5 ns | 273 |
| Small-word call loop, per iteration | 36.7 ns | 23 ns → 2.6 ns (1.2 ns `inline fn`) | 273, 280 |
| `match string.byte-at`, per byte | 93 ns | 1.8–2.0 ns | 280 |
| Lean User-Agent, typical Chrome header | 44.5 µs | 31 µs → 26.3–27.3 µs (25.3 µs inline helpers) | 273, 280 |
| 10,000 Int `list.append`s in a loop | 20.96 ms | 0.39 ms | 278 |
| 21 KB page with `html.raw` | 46.2 µs | 27.5–31.1 µs | 278 |
| `string.concat` / `bytes.xor` / `slice` per op | 63 / 76 / 63 ns | 18–21 ns | 279 |
| Mask one CSRF token | 274 ns | 177–189 ns | 279 |
| HMAC-SHA256, Linux OpenSSL 3.0.13 | 0.88–0.91 µs | 0.37 µs | 279 |
| Flow server, CPU per request (uncached) | 3.62 ms (276) | 2.61 ms (281) | 276–281 |
| Flow server, req/s (uncached / 32 MB cache) | 972 / 1,146 | 1,517 / 1,802 | 281 |
| Flow server PSS | 171 MB | 20 MB | 281 |

### Linux smoke runs (16 connections; sealed)

| Run | Server | Cache | Req/s | CPU / req | p50 / p99 ms | PSS MB | Loadgen CPU (user+sys) |
| --- | --- | --- | ---: | ---: | ---: | ---: | ---: |
| rust-nc-1 (273) | Rust | off | 1,134 | 2.97 ms | 13.5 / 28.9 | 84.6 | 3.4 s |
| rust-cache-1 (273) | Rust | response + fragment | 16,457 | 0.23 ms | 0.8 / 2.0 | 47.8 | 33.8 s (client-bound) |
| rails-nc-2 (273) | Rails | off | 16.0 | 190 ms | 934 / 1,598 | 706 | 0.5 s |
| rails-cache-1 (273) | Rails | redis fragments | 131.6 | 24.0 ms | 129 / 280 | 716 | 3.1 s |
| flow-nc-1 (276) | Flow | off | 1,126 | 3.62 ms | 10.9 / 35.7 | 171 | 23.3 s |
| flow277-off-1 (277) | Flow | off | 970 | 4.16 ms | 11.2 / 51.4 | 171 | 22.1 s |
| flow277-on-1 (277) | Flow | 32 MB | 1,093 | 3.67 ms | 9.5 / 45.1 | 171 | 26.3 s |

The 281 figures in the table above come from three interleaved runs per
configuration, made in the 281 worktree's `.agentlang/hostperf/`. That
directory was removed with the worktree, so its smoke JSON and `perf` reports
are **not** in this evidence; the numbers are quoted from docs/PENDING-REVIEW.md
("Linux profile (281)"). The concurrency sweep (report 282) re-measures every
server.

Two things these runs already show, which is why the sweep exists:

- **Rust at 16 connections is wait-bound,** not CPU-bound: 2.97 ms of CPU per
  request at 1,134 req/s is 84% of 4 vCPUs. Flow at 16 connections saturates
  them. Throughput at one connection count is not a fair comparison.
- **rust-cache-1 used Rust's completed-response cache** (its defaults: 64 MB
  response + 32 MB fragment). The plan compares fragment caches only, so that
  run is not the cached Rust figure. It was also client-bound.
- **The load generator spends far more CPU on Flow's responses** (about 2 ms
  per request) than on Rust's (0.3 ms), for pages of similar size. Not yet
  explained; the sweep records client utilization for every cell.

## Fairness

- All three servers run natively in WSL2 on the same snapshot data, 4 pinned
  vCPUs each, the load generator on disjoint vCPUs, no `User-Agent` header.
- The pages are not yet equivalent: Flow 460,238 bytes, Rust 416,127, Rails
  463,753 (rich text, two boosts and a few attributes are still missing in
  Flow and differ in Rust).
- Flow cannot log in (no bcrypt), so it serves the snapshot's Rails session;
  Rails and Rust log in at the start of each lifetime.
- Rails and Rust native builds differ from their Docker images in compiler and
  library versions (gcc 13 vs 14, glibc 2.39 vs 2.41, libvips 8.15 vs 8.16);
  native Rails is 7–17% slower than the 2026-10-13 Docker figures, with no
  known cause.

## Tests

- **Gates:** 25 gates on the combined branch at 58d2253 (run
  `gates-20261015T060358Z`, 63 min), all passing on their first run.
  This run adds `flowhttp` (384 assertions: counter app, interpreter vs native
  on Windows and Linux, trusted and diagnostic, cache checks) to 272's 24.
- **An earlier run was interrupted.** The first attempt
  (`gates-20261015T032218Z`, kept in the evidence as `gates-interrupted/`)
  passed its first four gates, then stopped in `owning-listbuild`, where
  `lld-link` exited with 0xC000026B (STATUS_DLL_INIT_FAILED) at the moment its
  session ended. No later gate wrote output and no summary was written. The
  rerun, with no code change, passed `owning-listbuild` (426 assertions), so the
  failure is put down to the session ending.
- Counts: LLVM suite 1,033, native conformance 1,033, owning templates 3,662,
  owning lists 1,725, stdlib 803, owning list build 426, owning html 359,
  owning list ops 226, owning UTF-8 222, owning compact 89, arena lifetime 55,
  Campfire views 15; Browser 3,842 (462-UA corpus with native O0/O2 parity);
  Kestrel HTTP 473; login attempt store 4,122; both native room-read profiles
  pass.

| Gate | Result | Seconds |
| --- | --- | ---: |
| llvm-full-suite | pass | 293 |
| arena-lifetime | pass | 0 |
| owning-compact | pass | 35 |
| owning-html | pass | 45 |
| owning-listbuild | pass | 78 |
| owning-utf8 | pass | 13 |
| owning-templates | pass | 33 |
| campfire-views | pass | 18 |
| acceptance | pass | 13 |
| flow-tests | pass | 22 |
| flow-runtime-tests | pass | 40 |
| ir-interpreter | pass | 5 |
| campfire | pass | 760 |
| native-value-stack | pass | 462 |
| owning-mailbox | pass | 85 |
| ir-parity | pass | 14 |
| native-conformance | pass | 268 |
| owning-lists | pass | 17 |
| owning-list-ops | pass | 47 |
| stdlib | pass | 638 |
| campfire-browser | pass | 108 |
| source | pass | 3 |
| flowhttp | pass | 47 |
| room-read-trusted-off | pass | 368 |
| room-read-diagnostic-off | pass | 374 |


## Next

1. **Report 282: the fair Linux sweep.** Rails, Rust and Flow at 16, 32, 64
   and 128 connections, uncached and with fragment caches only (Rust's
   response cache off), interleaved in one session, 3 reps, with the
   saturation labels (pre-registered in `.agentlang/linux-sweep-282/PLAN.md`).
2. **Open from the 281 profile:** trusted field lookups that take a String's
   extent from its length prefix and skip per-locate descriptor validation
   (~20% inclusive); a trusted-output flag so `cache.get` hits are not
   re-validated (8.6% of cached CPU); HTML template plan/write (~12%); arena
   copies of the 460 KB page (~5%); `session-row` off `db.column` (~1%).
3. **Equal pages:** rich text and boosts, so Flow, Rust and Rails serve the
   same bytes.
4. **Stdlib step 5:** delete the Windows HTTP.sys Campfire host and the .NET
   hosts; sign-in needs a bcrypt decision (not in BCrypt/libcrypto).
5. **Scheduled removal:** `string.length` alias, deadline 2026-11-01.
