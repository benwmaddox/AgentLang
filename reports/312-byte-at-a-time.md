# 312 — Code that works one byte at a time

After report 311, I profiled the uncached room page again and looked for code
that reads one byte at a time. Every change below gives the same answers:
- the 45 sampled responses are byte-identical;
- all suites pass;
- the new C code is checked exhaustively against the code it replaces.

## What was one byte at a time

| Where | Share of CPU (inclusive) | What it did |
| --- | ---: | --- |
| `rich-html.tag` | 6.9% | a tokenizer state machine reading every byte of every tag |
| `rich-html.scan` | 4.3% | every byte of message text, and of tag and attribute names and values |
| `campfireRoute.escape` | 1.9% | the generated check that a link parameter needs no percent-encoding, a byte at a time over ~80-byte avatar tokens |
| `al_owning_string_find`, `index-of`, `replace` | ~1.7% | compared the needle afresh at every position |
| `campfire.emoji.code-point?` | 0.8% | checked 78 ranges in turn for each code point |
| `al_cookie_scalar` | 0.6% | decoded each cookie byte with a 4-byte lookahead |
| `rich-html.last-slash` | 0.5% | read every byte of the open-element path |

Campfire's own text loops (`plain-text`, `scan-attachments`, `decode-text`,
`find-start-tag`) already jumped with `string.index-of`. Their cost is the
calls into `rich-html.tag` and the values they build, not byte stepping.

## 1. Two primitives to jump over runs

- **`string.next-byte-in(text, set, from)`** returns the first byte index at or
  after `from` whose byte is one of `set`'s bytes, or `byte-length` if there is
  none.
- **`string.next-byte-not-in(text, set, from)`** does the same for the first
  byte that is not in `set`.
- `from` is clamped to `0..byte-length`.

They are the C library's `strcspn` and `strspn` with a start position.
Natively, each is one runtime call that builds a 256-bit table from `set` and
scans; there are no arena copies and no Option. In the interpreter, they are
ordinary primitives.

**Checks.** Native parity now covers 109 primitives with 10,310 checks (from
6,780). The new cases include:
- texts with multi-byte characters;
- sets with non-ASCII bytes (`é`, U+00A0);
- `from` below 0, inside the text, at the end and past it;
- runs at O0, O2 and trusted O2, each equal to the interpreter.

A first version failed to link on Windows because the table's zero
initializer became a `memset` call, and the native modules link without the C
library; it now uses four explicit stores.

## 2. Byte loops jump over the bytes they only step past

Each loop now jumps from one byte it acts on to the next, and skips the bytes
that would only have taken the "advance by one" branch:
- **`rich-html.scan`:**
  - plain text jumps to the next `<`, `>`, `&` or the first byte of U+00A0;
  - tag and attribute names are one `next-byte-not-in` over the name
    characters;
  - attribute values jump from `&` to `&` until the closing `"`.
- **`rich-html.tag`:** tag names, attribute names and attribute values jump to
  the next byte that changes the tokenizer's state.
- **`rich-html.last-slash`:** jumps from `/` to `/`.
- **The generated route `escape`:**
  - the clean check is one `next-byte-not-in` over the unreserved bytes;
  - when encoding is needed, each clean run is one slice instead of one slice
    per byte.
- **`campfire.emoji.code-point?`:** a binary search over the sorted range
  pairs, about 7 steps instead of up to 78. New attached tests check that the
  pairs stay sorted and test the first and last code point of the list, plus
  the values just outside it.

**Speed** (ABBA against report 311's build):

| | 1 A | 2 B | 3 B | 4 A |
| --- | ---: | ---: | ---: | ---: |
| Uncached req/s | 3,081 | 3,112 | 3,145 | 3,130 |
| Uncached CPU ms per request | 1.403 | 1.376 | 1.377 | 1.390 |
| Cached req/s | 9,053 | 9,128 | 9,125 | 9,542 |
| Cached CPU ms per request | 0.479 | 0.473 | 0.475 | 0.454 |

- **Uncached: about 1% more requests a second, and 1.4% less CPU per
  request.**
- **Cached:** within noise. Run 4A was a fast lifetime.

The gain is small. Most of `rich-html.tag`'s time goes to the slices,
`to-lower-ascii` copies and list appends it makes for each attribute, not to
stepping through bytes.

## 3. C: substring search and cookie decoding

**Substring search.** `string.contains`, `string.index-of` and
`string.replace` compared the needle in full at every position. They now share
`al_owning_find_bytes`:
- **The 8-byte step.** It finds candidates for the needle's first byte 8 bytes
  at a time, with the same zero-byte word test as report 310's escaping.
- **Exact candidates.** The lowest set bit of the test always marks a real
  match. A borrow can only mark bytes after one.
- **Compare only there.** The full needle is compared only at those
  positions.
- **Checks.** It matched the naive search on 780,368 inputs, at O2 and under
  UBSan (`string_find_test.c`, now in `Test-NativeRuntime.ps1`). The inputs
  were:
  - a needle planted at every position, with every start;
  - near misses;
  - random texts over a two-letter alphabet;
  - high bytes, which make borrows cross bytes.

**Cookie decoding.** Two shortcuts, both exact because a String is always valid
UTF-8:
- a value with no `%` and no `+` decodes to itself, so it is copied as it is;
- otherwise, a plain ASCII byte is its own character and skips the 4-byte
  lookahead.

**Speed** (ABBA against step 2):

| | 1 A | 2 B | 3 B | 4 A |
| --- | ---: | ---: | ---: | ---: |
| Uncached req/s | 3,254 | 3,371 | 3,381 | 3,270 |
| Uncached CPU ms per request | 1.334 | 1.291 | 1.288 | 1.318 |
| Cached req/s | 9,651 | 9,669 | 9,438 | 9,470 |
| Cached CPU ms per request | 0.449 | 0.449 | 0.457 | 0.458 |

- **Uncached: 3.5% more requests a second, and 2.8% less CPU per request.**
- **Cached:** unchanged.

**A measurement caveat.** A repository-wide file search I had started ran on
the Windows host until partway through these runs, and through report 311's
route and SQLite runs. The ABBA order cancels a steady load, and each pair's A
and B runs agree with each other. But this step's A runs (3,254 and 3,270) are
about 4% faster than step 2's B runs of the same binary, which fits the search
ending in between. So compare runs within a pair, not across pairs.

## 4. Literal sets get their byte table at compile time

**Why.** The first profile after steps 2 and 3 showed
`al_owning_string_next_byte` at 3.1% of CPU, and each call rebuilt its 256-bit
table from `set`. Every call site passes a literal set.

**What changed.**
- **Literal sets.** When `set` is a String literal, the compiler builds the
  table and passes it as four 64-bit constants to
  `al_owning_string_next_byte_table`. A set computed at run time still builds
  its table per call.
- **Two call sites rewritten.** One in `rich-html.tag` picked its set at run
  time. One in `rich-html.scan` had an `if` expression between the literal and
  the call, which moves the literal. Both now pass a literal at the call, so
  all 14 call sites use compile-time tables.
- **Checks.** Native parity adds literal-set words, including non-ASCII bytes,
  so the compiler's table is checked against the interpreter: 10,972 checks.

**Speed** (ABBA against step 3):

| | 1 A | 2 B | 3 B | 4 A |
| --- | ---: | ---: | ---: | ---: |
| Uncached req/s | 3,310 | 3,316 | 3,312 | 3,268 |
| Uncached CPU ms per request | 1.286 | 1.275 | 1.278 | 1.291 |
| Cached req/s | 9,877 | 9,473 | 9,674 | 9,914 |
| Cached CPU ms per request | 0.447 | 0.444 | 0.448 | 0.445 |

- **Uncached: about 1% more requests a second, and 1% less CPU per request.**
- **Cached:** CPU per request is unchanged; throughput moved within noise.

So most of that 3.1% was the scanning itself, not building the table.

## Together

| Step | Uncached | Cached |
| --- | ---: | ---: |
| 2. byte loops jump over runs (Flow) | +1% | noise |
| 3. 8-byte substring search, cookie shortcuts (C) | **+3.5%** | 0 |
| 4. compile-time byte tables | +1% | 0 |

The uncached page went from about 3,106 to about 3,314 req/s (+6.7%; the end
points are from different runs), and from 1.40 to 1.28 ms of CPU per request.

## Where the time goes now

Profiled with the profiling build, on the server's own samples (`categorize.py`
groups each sample by its innermost function). The profiling build is slower
than the real one, so these are shares, not absolute times.

**Uncached room page** (1.28 ms of CPU per request):

| Share | Kind of work | Largest pieces |
| ---: | --- | --- |
| 19.4% | **value checks**: scanning, measuring and locating values in the arena | `scan_value` 8.9, `scan_text` 4.1, `validate_type_descriptor` 1.9, `locate_sum_case` 1.6 |
| 14.4% | compiled Flow code itself | `rich-html.tag` 3.3, `allowed-tag?` 1.0, `rich-html.scan` 0.8 |
| 11.3% | SQLite | the VM, B-tree seeks, `strftime` formatting |
| 10.5% | copying values | `copy_range` 5.4, `copy_constant` 3.8, `bytes_move` 1.4 |
| 9.1% | arena bookkeeping | `loop_relocate` 3.2, `update_live` 2.0, `reserve_to` 1.2 |
| 7.9% | HTML templates and escaping | `template_write` 3.6, escaping 3.0 |
| 7.9% | stdlib dispatch | `al_stdlib_emit` 2.1 and the generic call path |
| 7.5% | string operations | concat, equality, `next_byte_table` 1.1 |
| 6.8% | kernel: sockets, scheduling | sending the 417 KB page |
| 0.8% | crypto (libcrypto) | avatar and cookie HMACs |

**What is behind those:**
- **Value checks are spread out.** No word owns more than 1.6% of them; the
  top ones are `page-query`, `whole-room-page`, `campfireRoute.path` and
  `rich-html.tag`. They are what remains of measuring and locating values
  whose shapes the compiler does not compute inline: sums, nested records and
  Lists.
- **Copying:** `rich-html.tag` 1.4% (its slices and attribute records);
  `http-response` and `show-room` 1.2% each (assembling the response);
  `whole-room-page` 1.1%.
- **Loop relocation:** `rich-html.tag` 1.3% (its `attributes` list) and
  `whole-room-page` 0.9% (the 40 messages).
- **Templates:** 4.7% is the message template (`rails-message-shown`), and
  1.3% is the page around it.
- **By Flow word** (inclusive):
  - `page-query` 11.7% (SQL and decoding the rows);
  - `rich-html.tag` 9.6%;
  - `rails-message-shown` 8.7%;
  - `rich-html.scan` 3.7%;
  - `plain-text` 3.1%;
  - `campfireRoute.path` 2.7%, the route-table links from report 311.

**Cached room page** (0.45 ms of CPU per request):

| Share | Kind of work | Largest pieces |
| ---: | --- | --- |
| 26.3% | **value checks** | `scan_value` 8.7, `measure_external_value` 6.9, `validate_type_descriptor` 3.4 |
| 18.0% | SQLite | the page-keys query |
| 15.1% | kernel: sockets, scheduling | |
| 7.9% | compiled Flow code | `worker-cache.find`, `cache.key`, `store.int-at` |
| 5.7% | arena bookkeeping | |
| 5.6% | string operations | concat |
| 5.1% | libc `memmove` | copying cached fragments out |
| 4.2% | stdlib dispatch | cookies, JSON, base64 |

**The cached page's biggest single cost is the cache lookup re-checking its
own values.**
- **What the lookup costs.** `worker-cache.find` accounts for about 11% of
  CPU, mostly value checks: `measure_external_value` 5.3% and the scans 5.8%.
- **Why.** A cached fragment lives outside the request's arena, so every read
  takes the full external check, as if a host had handed it in. Our own
  verified code wrote it.
- **The lead.** Checking each fragment once when it is stored, or trusting
  values the worker's own code put in the cache, would remove most of that.
  It is the clearest next lead, and the kind of change that needs care, so it
  is left for you to decide.

## Not done

- **The cache re-checking its values**, above.
- **`rich-html.tag` still builds values.** Each attribute's name and value are
  copied out as Strings and appended to a list (relocation 1.3%, copying
  1.4%). A tokenizer that returns offsets instead would cut both, but it
  changes the `HtmlTag` type the rest of `rich-html` uses.
- **SQLite's `strftime`.** The page query formats four timestamps per row in
  SQL (`sqlite3_str_vappendf`, 0.5% uncached and 1.0% cached). Computing the
  milliseconds in Flow from the ISO text would remove it, but it has to match
  SQLite's rounding.
