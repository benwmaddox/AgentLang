# 314 — The cached room page, end to end

The cached room page is the room page served with the worker store on: each
message's rendered fragment is kept in a per-worker cache and written straight
to the response on a hit. After report 313, it costs 0.43 ms of CPU per
request. This report follows one cached request through, fixes what Campfire
did twice, and lists what is left, most of it outside Campfire's Flow code.

## The path of a cached request

1. **Session:** restore the session from its cookie (Base64, an HMAC check
   with libcrypto, a session row).
2. **Membership:** check that the user belongs to the room.
3. **Page keys:** two SQL statements. One checks that the room exists. The
   other reads the newest 40 messages' ids and `updated_at`, which are all the
   fragment keys need.
4. **Lookups:** for each of the 40 messages, build its fragment key and look
   it up in the worker cache.
5. **Misses:** load the missed messages and their boosts, then render them.
6. **Response:** write the page head, then each fragment (a hit straight from
   the store, a miss as rendered), then the tail. Store the rendered misses.

**Where its CPU went** (report 313's profile, server samples only):

| Share | What |
| ---: | --- |
| 20% | SQLite, of which **17% is the page-keys query** |
| 18% | the kernel, sending the 417 KB page |
| 9.3% | `worker-cache.find` |
| 7.0% + 5.9% | `cache.key` and `fragment-key`, building the keys |
| 4.6% | `worker-cache.write-to-response` |
| 2.7% + 2.0% + 1.1% | session restore, cookie parsing, token check |

## 1. One key and one lookup per message (Campfire)

**What was done twice.** The page built every fragment key and searched the
cache for it twice:
- `missing-ids` did it once, to know which messages to load;
- `write-messages` did it again, through `worker-cache.write-to-response`.

That is 80 keys and 80 lookups for a 40-message page.

**The fix.**
- **`page-lookup`** builds each key once and keeps the entry it found, or a
  `CacheEntry` with length −1 for a miss. A plain record has a fixed size, so
  `list.get` on the entries is one constant-stride step (report 310); an
  `Option` per entry would have meant a walk. `write-messages` writes each hit
  straight from its entry.
- **Why the entries stay valid.** An entry is good until the next put on the
  cache, and the page's puts come only after the page is written.
- **`fragment-key`** is now one `string.join`, not five concatenations through
  `cache.key`. It produces the same text, and a new attached test compares the
  two.

**Checks:** all 45 sampled responses are byte-identical, including the first
(all-miss) and repeated (all-hit) room requests with the store on. Campfire's
attached tests and the Campfire suites pass.

**Speed** (ABBA against report 313's build, run entirely inside WSL under the
shared lock, on a quiet machine; see below):

| | 1 A | 2 B | 3 B | 4 A |
| --- | ---: | ---: | ---: | ---: |
| Cached req/s | 10,127 | 10,105 | 10,197 | 9,991 |
| Cached CPU ms per request | 0.396 | 0.396 | 0.393 | 0.401 |
| Uncached req/s | 3,274 | 3,297 | 3,306 | 3,282 |
| Uncached CPU ms per request | 1.229 | 1.219 | 1.218 | 1.221 |

- **Cached: about 1% more requests a second, and 1% less CPU per request.**
- **Uncached:** unchanged, within noise. The uncached page does not run this
  code.

**A noisier earlier run.** An earlier run of the same pair showed the cached
page at +2.6%. It ran while other work loaded the machine, and its uncached
lifetimes rose steadily from 2,730 to 3,314 req/s, so its numbers are not
used. On the quiet machine, both builds are also faster in absolute terms:
0.40 ms per cached request, against 0.43–0.45 ms in the noisy runs. Report
313's cached numbers were measured before the contention and stand.

The gain is smaller than the profile shares suggested. After report 313
removed the layout walk from every host result, each lookup was already
cheap. What remained was mostly the host calls themselves, and halving them
saves about 1%.

## 2. Benchmarks run entirely inside WSL, one at a time

**What was in WSL already.** The server binary, the SQLite database copy, the
config and the load generator ran from WSL's own file system.

**What still touched `D:` during a run:**
- the scripts;
- the inputs copied in at setup: the snapshot, the config secret, the cookie
  and the contract file;
- every log line and result.

**Now `abpair-wsl.sh`:**
- stages all of these under `/home/ben/linuxbench/bench-stage` (private, owned
  by `ben`);
- runs the ABBA from there;
- copies the results to `.agentlang/reopen-300` at the end.

**Other sessions benchmark on the same cores.** Two crudspec sessions use the
same pinned cores (server 8–11, load generator 12–19). An overlap made
report 313's last ABBA and my first attempt here unreadable. We agreed:
- **A shared lock.** Each whole ABBA holds an exclusive
  `flock /home/ben/linuxbench/bench.lock`.
- **A per-lifetime check.** Before each lifetime, a check waits while another
  benchmark's lifetime, load generator or server is running. It matches only
  process names, never a waiting script's command line; matching command lines
  caused two short deadlocks before the check was narrowed.

## What is left in the cached path

1. **The page-keys query, 17% of cached CPU.** For each of the room's 131
   messages, SQLite looks up the table row through `index_messages_on_room_id`
   to read `created_at`, then sorts them to keep the newest 40. Measured on a
   copy of the snapshot in WSL, with Python's sqlite3 3.45:
   - the whole query takes about 60 µs;
   - the id-and-sort part alone takes about 37 µs;
   - formatting the timestamps is negligible.

   Any rewrite of the query still has to read and sort those rows. A covering
   index on `messages (room_id, created_at)` would turn it into a short index
   range read. **That changes the schema shared with the Rails and Rust
   references, so it is your decision.**
2. **The kernel, 18%:** sending a 417 KB page through a socket, which zeroes
   buffer pages and copies the bytes. This is inherent in serving the page
   uncompressed, as the references do.
3. **Session restore, about 6%:** cookie parsing, Base64, the HMAC check and
   the session row. Each part is small.
4. **`cache.key` builds keys by concatenating in a loop.** It is no longer on
   the room page's path (`fragment-key` joins directly). Other callers of the
   library function still pay for the growing string moved on each iteration.
