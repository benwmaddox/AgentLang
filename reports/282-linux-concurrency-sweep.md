# 282 — Fair Linux sweep: Rails, Rust and Flow at 16–128 connections, cached and uncached

The benchmark the 273–281 work was for. All three Campfire servers run natively
in WSL2 on the same snapshot data, each on 4 pinned vCPUs. The comparison is
the room page, swept over 16, 32, 64 and 128 connections, uncached and with
message-fragment caches only. The 18 lifetimes ran interleaved in one session
(3 reps × 6 configurations), with per-cell saturation checks. The protocol was
pre-registered before the first run (`PLAN.md` in the evidence).

## Setup

- **Servers:**
  - Rails: native Ruby 3.4.10 + YJIT + jemalloc, puma 3 workers × 5 threads, redis, as in the reference image.
  - Rust: native, reference 6dae2fd.
  - Flow: Campfire on the portable Flow host, built at bb80a34, trusted profile, O2, no `-Profile`; binary sha256 `408a99d8…2744`.
- **Uncached:** Rails uses the no-cache initializer; Rust runs with `CAMPFIRE_RESPONSE_CACHE_MB=0` and `CAMPFIRE_FRAGMENT_CACHE_MB=0`; Flow runs with `--cache-mb 0`.
- **Cached, fragment caches only:** Rails uses `redis_cache_store`; Rust runs with `CAMPFIRE_FRAGMENT_CACHE_MB=32` and `CAMPFIRE_RESPONSE_CACHE_MB=0`; Flow runs with `--cache-mb 32`. Each lifetime read these settings from the live process (`cache-env.txt`), and all 18 matched the plan.
  - 273's `rust-cache-1` ran Rust's defaults, which include the 64 MB completed-response cache. That made it 16,457 req/s at 0.23 ms, so it is not comparable.
- **Placement and load:**
  - Server on vCPUs 8-11, load generator on vCPUs 12-19.
  - No `User-Agent` header, for any server.
  - Every response is validated against the canonical room_show contract (40 messages).
- **Each cell:** a warm-up at the same connection count (Rails 10 s, others 3 s), then a measured window (Rails 30 s, others 10 s).
  - Server utilization = cgroup CPU / (secs × 4).
  - Client utilization = loadgen CPU / (secs × 8).
  - A cell is saturated if the server is at ≥ 0.90 and the client is under 0.70. It is client-bound if the client is at ≥ 0.70. Otherwise it is wait-bound.
- **Validity:** 72 cells, 0 invalid responses, 0 errors, every status 200, and every login contract matched (ids and content).

## Results (median [range] over 3 reps)

### Uncached

| Server | Conn | Req/s | CPU ms/req | p50 / p99 ms | Server util | Client util | Label | PSS MB |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | --- | ---: |
| Rails | 16 | 20.9 [20.7-21.2] | 153.8 [149.7-155.1] | 744 / 1,000 | 0.80 | 0.01 | wait-bound | 815 |
| Rails | 32 | 20.9 [20.8-21.0] | 151.0 [150.6-153.7] | 1,522 / 1,904 | 0.79 | 0.01 | wait-bound | 811 |
| Rails | 64 | 21.0 [20.3-21.2] | 152.8 [148.2-155.2] | 3,062 / 5,374 | 0.79 | 0.01 | wait-bound | 819 |
| Rails | 128 | 20.8 [20.6-20.9] | 150.8 [149.8-152.0] | 5,923 / 12,362 | 0.78 | 0.01 | wait-bound | 834 |
| Rust | 16 | 1,561 [1,522-1,660] | 2.39 [2.38-2.40] | 9.8 / 20.7 | 0.93 | 0.04 | saturated | 90 |
| Rust | 32 | 1,573 [1,528-1,583] | 2.39 [2.37-2.39] | 19.6 / 39.4 | 0.94 | 0.04 | saturated | 90 |
| Rust | 64 | 1,561 [1,544-1,572] | 2.42 [2.42-2.44] | 39.2 / 78.3 | 0.94 | 0.04 | saturated | 93 |
| Rust | 128 | 1,537 [1,495-1,587] | 2.45 [2.44-2.47] | 81.0 / 132.5 | 0.94 | 0.04 | saturated | 98 |
| **Flow** | 16 | **1,986** [1,973-2,100] | **2.06** [2.04-2.07] | 5.4 / 15.2 | 1.02 | 0.54 | saturated | **20** |
| Flow | 32 | 1,916 [1,915-1,966] | 2.07 [2.06-2.08] | 13.6 / 39.0 | 0.99 | 0.54 | saturated | 20 |
| Flow | 64 | 1,953 [1,941-2,020] | 2.07 [2.04-2.08] | 28.0 / 77.5 | 1.02 | 0.54 | saturated | 20 |
| Flow | 128 | 1,955 [1,936-1,973] | 2.09 [2.06-2.10] | 60.4 / 133.6 | 1.02 | 0.55 | saturated | 20 |

### Fragment caches

| Server | Conn | Req/s | CPU ms/req | p50 / p99 ms | Server util | Client util | Label | PSS MB |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | --- | ---: |
| Rails | 16 | 169 [166-169] | 18.7 [18.5-19.3] | 92 / 280 | 0.79 | 0.04 | wait-bound | 779 |
| Rails | 32 | 163 [157-164] | 19.8 [19.7-20.0] | 196 / 326 | 0.80 | 0.04 | wait-bound | 779 |
| Rails | 64 | 150 [150-154] | 21.3 [20.9-21.4] | 389 / 593 | 0.80 | 0.04 | wait-bound | 763 |
| Rails | 128 | 149 [144-149] | 21.4 [21.2-21.5] | 880 / 1,016 | 0.79 | 0.04 | wait-bound | 754 |
| **Rust** | 16 | **9,855** [9,210-9,924] | **0.40** [0.40-0.41] | 1.5 / 2.8 | 1.00 | 0.39 | saturated | 48 |
| Rust | 32 | 9,821 [9,711-10,010] | 0.40 | 3.1 / 5.8 | 0.99 | 0.35 | saturated | 50 |
| Rust | 64 | 9,848 [9,830-9,961] | 0.40 | 6.2 / 11.6 | 0.99 | 0.33 | saturated | 52 |
| Rust | 128 | 9,734 [9,620-9,953] | 0.40 | 12.7 / 20.3 | 0.98 | 0.33 | saturated | 57 |
| Flow | 16 | 2,332 [2,248-2,383] | 1.58 [1.57-1.66] | 4.1 / 15.5 | 0.92 | 0.63 | saturated (1 rep wait-bound) | 20 |
| Flow | 32 | 2,389 [2,357-2,425] | 1.70 [1.69-1.72] | 10.2 / 30.9 | 1.01 | 0.68 | saturated | 20 |
| Flow | 64 | 2,380 [2,341-2,402] | 1.68 [1.68-1.70] | 22.4 / 55.2 | 1.00 | 0.69 | saturated | 20 |
| Flow | 128 | 2,389 [2,282-2,490] | 1.70 [1.70-1.71] | 47.4 / 115.6 | 1.02 | 0.71 | 1 rep client-bound | 20 |

### Peaks (highest median req/s among saturated cells)

| Server | Uncached | Fragment cache |
| --- | --- | --- |
| Rails | not saturated (wait-bound): 21.0 req/s, 153 ms/req | not saturated (wait-bound): 169 req/s, 18.7 ms/req |
| Rust | 1,573 req/s at 32 conn, 2.39 ms/req | 9,855 req/s at 16 conn, 0.40 ms/req |
| Flow | 1,986 req/s at 16 conn, 2.06 ms/req | 2,389 req/s at 32 conn, 1.70 ms/req |

## Findings

- **Uncached, Flow beats Rust.** Flow serves 26% more requests per second
  (1,986 vs 1,573) at 14% less CPU per request (2.06 vs 2.39 ms) and less than
  a quarter of the memory (20 vs 90 MB PSS), with lower p50/p99 at every
  connection count. Both servers are saturated, and their CPU per request
  barely moves from 16 to 128 connections. Flow's page is 11% larger (below),
  so the per-byte comparison favors Flow further.
- **With caches, Rust is 4.1× ahead** (9,855 vs 2,389 req/s; 0.40 vs 1.70
  ms/request). The cache saves Flow only 17% (2.06 → 1.70 ms), against 83% for
  Rust (2.39 → 0.40). This is the clearest target. Two causes are known, and
  the second is a fairness question:
  1. Flow's cached path still pays for page-query, re-validation of every
     `cache.get` hit (8.6% of cached CPU in the 281 profile) and the
     per-request layout.
  2. **Rust's page is byte-identical on every request** (`body_constant=yes`
     in all 6 Rust lifetimes). Rails' and Flow's pages differ on every
     request, because the composer and meta CSRF tokens are freshly masked.
     Rust apparently does not re-mask per-request tokens, so it does less work
     per request than Rails. This needs checking in the Rust reference before
     the cached comparison is called fair.
- **Rails never saturates its 4 vCPUs.** It sits at 0.78–0.81 utilization at
  every connection count, cached or not. The reference configuration runs 3
  puma workers, each limited by the GVL, so about 3 cores' worth of Ruby. It is
  wait-bound by its own configuration, not by the client. Its CPU per request
  (151 ms uncached, 19 ms cached) is still the fair per-request cost: Flow uses
  73× less CPU per request uncached and 11× less cached. Requests per second
  are not comparable for Rails.
- **The load generator works harder on Flow and Rails pages.** It reuses a
  validation result when a response is byte-identical to the previous one
  (`loadgen/src/validation.rs`). Rust's constant page is checked once per
  connection, while Flow's (and Rails') are fully checked every time: client
  utilization 0.54–0.71 for Flow against 0.33–0.39 for Rust at 4–5× the
  request rate. This counts against Flow in the labels (one Flow cached
  128-connection rep crossed 0.70). It does not change server CPU per request.
- **Rust uncached is CPU-bound once the client has 8 vCPUs.** The 273 smoke
  (load generator on 4 vCPUs) looked wait-bound at 2.97 ms/request. Here Rust
  is at 0.93–0.94 utilization and 2.39 ms.

## Fairness

- **Pages are not identical.** Flow sends 460,238 bytes, Rust 416,127 and Rails
  463,753. Flow still lacks rich text, two boosts and a few attributes; Rust
  differs as noted above.
- Flow cannot log in (no bcrypt), so it serves the snapshot's Rails session
  cookie. Rails and Rust log in at the start of each lifetime.
- vCPUs, not physical cores. All three servers ran in the same WSL2 VM and the
  same session, with the order rotated per rep and the connection order rotated
  per lifetime.
- **Contamination screen.**
  - Windows load was 2% before the start; the Codex app was open and idle.
  - The first lifetime's start line shows systemd at 19% and dockerd at 13%.
    These are `ps` lifetime averages on a freshly started VM (15-min loadavg
    0.24), under the pre-registered 20% threshold, so nothing was excluded.
  - Every later lifetime shows < 1% foreign CPU.
- Two shakedown lifetimes (`shake-rust-on`, `shake-flow-off`) checked the
  script before the sweep. They are kept in the evidence and are not part of
  the results.

## Next

1. **Cached Flow.**
   - Trusted-output flag so `cache.get` hits skip UTF-8 re-validation.
   - Cache more of the page per request, as Rust evidently does, once the CSRF
     question below is settled.
   - Profile the cached path again.
2. **Rust CSRF masking.** Check whether the Rust reference re-masks
   per-request CSRF tokens as Rails does. If it doesn't, decide whether to
   compare against it as-is or with masking.
3. **Rails at 4 workers** (`WEB_CONCURRENCY=4`), as a supplementary run so
   Rails can use all 4 vCPUs. Needs a user decision: it changes the reference
   configuration.
4. The 281 profile items for uncached Flow: field lookups (~20%) and templates
   (~12%).
