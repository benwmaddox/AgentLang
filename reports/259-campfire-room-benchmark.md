# 259 — First room-page benchmark: AgentLang AOT, Rails and Rust on the shared harness

Status: measured and contract-valid. **Not yet apples-to-apples in behaviour.** The
AgentLang page is a contract-minimal 10 KB page, while Rails and Rust render the
full ~420–460 KB Campfire page. Read every number together with the page column.

## Setup

- **Subjects:**
  - the LLVM AOT native host (O2, TrustedGenerated, stage profiling off, 4
    workers);
  - the Rails reference (pinned `90b3300` image);
  - the Rust reference (`6dae2fd` image).

  The interpreter is not benchmarked.
- **Workload:** the shared harness's Rails-built seed, `GET /rooms/<watercooler>`
  as user `david`, with a Rails-issued session for every server. Every response is
  checked by harness loadgen `--validate` against the Rails-derived `room_show`
  contract: the exact newest-40 window from seed SQL, Rails DOM identities and
  the seeded content tokens. Each reference's own contract matched it exactly.
- **Timing:** harness defaults. A 2 s warmup at 4 clients, then a timed 8 s run at
  16 clients. Two balanced `N R S S R N` sets give 4 lifetimes per server, each
  with fresh storage.
- **Placement:**
  - native runs on Windows HTTP.sys, pinned to four distinct physical cores
    (`0x5401`), with a Windows loadgen pinned to `0x3F03FC`;
  - the references run in Docker on WSL2 with `--cpuset-cpus 8-11`, and a Linux
    loadgen (same source and lock file) runs inside the server's network
    namespace on CPUs 12–15;
  - the CPU budgets are not equivalent (physical P-cores versus Hyper-V vCPUs),
    so CPU per request is reported but no CPU-normalized speedup is claimed.
- **Protocol:** written before collection (`benchmark-protocol-259.md` in the
  evidence), including the shakedown, flag rules and later amendments.

## Primary result: no response or fragment caching (user direction)

Caches were found by source inspection:
- **AgentLang native:** none.
- **Rust:** a completed-response cache and a fragment cache, both disabled with
  `CAMPFIRE_RESPONSE_CACHE_MB=0` and `CAMPFIRE_FRAGMENT_CACHE_MB=0`.
- **Rails:** fragment and collection caching, disabled by a read-only mounted
  initializer that sets `perform_caching = false`. Its Redis key count stayed at 6
  through every run.

SQLite page caches and prepared-statement reuse remain in all three.

| Server | Page decoded / wire | Median req/s (4 lifetimes) | Spread | Median p99 | Server CPU per request |
| --- | --- | ---: | ---: | ---: | ---: |
| AgentLang AOT | 10,073 / 10,073 B | **4,764** | 9.3% | 4.4 ms | 0.80 ms |
| Rust, identity | 416,127 / 416,127 B | 1,322 | 23.1% | 25.9 ms | 2.93 ms |
| Rust, gzip offered | 416,127 / 24,216 B | 987 | 16.9% | 33.1 ms | 4.10 ms |
| Rails, identity | 463,753 / 463,753 B | 19 | 15.7% | 1,108 ms | 176 ms |
| Rails, gzip offered | 463,753 / ~44,350 B | 17 | 7.2% | 1,833 ms | 187 ms |

All 24 timed samples had zero invalid responses and zero errors, with all
statuses 200. No sample was flagged: servers were saturated and clients ran at or
below 0.42 cores. Native made no session writes in any lifetime.

## Secondary result: references with default caching

| Server | Median req/s | Spread | Median p99 | Server CPU per request | Note |
| --- | ---: | ---: | ---: | ---: | --- |
| AgentLang AOT (no cache) | 4,984 | 2.3% | 4.2 ms | 0.79 ms | |
| Rust, identity | 16,643 | 16.0% | 2.0 ms | — | client-bound (3.4 cores), lower bound |
| Rust, gzip offered | 30,345 | 6.0% | 1.1 ms | 0.13 ms | cached, pre-compressed page |
| Rails, identity | 159 | 6.6% | 221 ms | 21 ms | |
| Rails, gzip offered | 157 | 6.1% | 264 ms | 21 ms | |

With its default caches, Rust mostly serves a stored completed response, so that
row measures cache delivery, not page generation. Cache-enabled approaches for
all three servers are a later comparison.

## Why this is not yet apples-to-apples

- **Page behaviour.** 92% of the Rust page (384 KB) is the 40-message region,
  about 9.6 KB per message. Each message has:
  - a day separator and local-time tags;
  - an avatar link and image;
  - author, permalink and room link;
  - an actions menu with 8 quick-boost forms, a boost link, reply and copy
    buttons, and a permission-dependent edit link;
  - the Lexxy/ActionText rich-text body;
  - a boosts strip.

  The page also has `<head>` assets, the sidebar and the composer. AgentLang
  renders about 150 bytes per message (identity, client ID and escaped body)
  plus a minimal shell. Its stored rich text stays HTML-escaped because no
  Rails-equivalent sanitizer exists yet.
- **Platform.** Windows HTTP.sys versus Linux containers in a VM. Kernel network
  work is outside every CPU figure.

So the AgentLang throughput reflects a much lighter page. **No speed ratio is
claimed.** Reaching behavioural parity means rendering the full Rails room page
and its data (users, avatars, boosts, permissions, rich-text rendering, layout).
That is the next migration step, and the comparison will be rerun with this
method once it is done.

## Other findings

- The harness's own `search` contract fails against Rails on this seed. Rails
  and the harness SQL return the same 13 IDs, but the harness orders by `id`
  while Rails orders by `created_at`, and message `933434483` has a later
  `created_at` than its ID rank. `room_show` orders by `created_at` and is
  unaffected.
- The harness checks pass locally: fmt, clippy, 9 cargo tests, 4 Ruby test files
  and 4 Node syntax checks.
- Retained and excluded:
  - the shakedown lifetimes;
  - `explore-rust-nocache-1/2`, which actually ran with caches **on** because a
    driver edit failed.
- The user's unrelated Postgres container was never contacted. All servers use
  SQLite on Docker's default bridge network. A read-only `docker stats` listing
  that mentioned it was removed from the driver and from the evidence.

## Sealed evidence

[Archive](evidence/259-campfire-room-benchmark/259-campfire-room-benchmark-evidence.zip)
and [manifest](evidence/259-campfire-room-benchmark/manifest.json): 462 entries,
297,631 bytes, SHA256
`a02e4b60e081159534c5d7aa8ee4ee9b25f52bb311a0890fc4992a7348eca550`. Contents:

- every lifetime's driver log, loadgen results and CPU receipts;
- the protocol and its amendments, both aggregations, all drivers and the
  page-anatomy tools;
- the image and loadgen build logs and contract hashes;
- per-run summaries of the container request logs (counts and status totals),
  replacing the 1.4 GB of raw per-request logs.

Excluded: page bodies, databases, credentials, cookies, seed labels and contract
contents (hashes only).
