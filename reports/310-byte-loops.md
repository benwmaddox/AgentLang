# 310 — Low-risk speed: list walks, text checks, integer loads, escaping

The question was where byte-at-a-time and per-element work still costs time,
fixed only in ways that cannot change an answer. Each step below is checked
three ways:
- all 45 sampled responses byte-identical to the build before (report 300's
  bytecmp);
- all 25 suites;
- an ABBA run against the step before it (report 285 protocol: WSL2, 4 pinned
  vCPUs, sweep 16/32/64/128).

## Where the time was

The profile of the uncached room page after report 308:

| Cost | Share of CPU | Mostly from |
| --- | ---: | --- |
| `al_owning_measure_value`, with the scans under it | 15.5% | list walks: `rich-html.tag` 6.8%, `campfire.emoji.code-point?` 3.0%, `rich-html.allowed-tag?` 2.1% |
| `al_owning_scan_text` (self) | 7.0% | its O(1) trusted path, called from those measures; the time is call count and the first touch of each String header |
| `al_owning_html_template_write` | 5.4% | its 16-byte copies and the escape loop, one byte at a time |
| `string.replace` plan + write | 3.9% | `rich-html.text-ok-for?`'s blank test (2.3%) |
| `al_owning_load_i64` / `store_i64` | 2.3% | one byte per loop step |

`string.byte-at-or` was already inline in the generated code, so the Flow
byte loops make no runtime call per byte.

## Step 1: list walks size elements inline

**What was slow.**
- **Every walk measured every element.** A `for` loop over a List, `map` /
  `filter` / `fold` / `each`, and `list.get`'s walk to its index all called
  `al_owning_measure_value` for each element.
- **The measure is the general one.** It checks the layout header, then scans
  the value field by field, through `scan_value` and `scan_text`.
- **The worst cases:**
  - `List<Int>`: every element is 8 bytes, yet `emoji.code-point?` measured
    each of its 156 Ints for every code point;
  - `rich-html.tag`'s duplicate-attribute check: it walks
    `List<HtmlAttribute>`, a record of two Strings, so each step made a
    record scan plus two String scans.

**What changed.** Trusted builds compute the element's size from the static
layout, the same arithmetic the trusted scan does:
- a fixed-size type is a constant;
- a String or Bytes is its padded length header, `(8 + length + 7) & ~7`;
- a record of those sums its fields, and zero-width fields add nothing.

The element must still end inside its list, or the request fails as before.
Sums and Lists keep the runtime call, and so do diagnostic builds. In Campfire,
138 of the 177 measure call sites became inline.

**Checks.** `AgentLang.OwningListBuild.Tests` now walks lists of Ints, of
Strings, and of a record. The record has String, Int, fixed-record and
String-scalar fields. Each walk runs:
- with 0, 1 and 9 elements, and with String lengths 0–17, covering every
  padding remainder;
- as a `for` loop, a `map` and a `list.get`, at indexes −1, 0, middle, last
  and past the end;
- in diagnostic O0 and O2 and trusted O0 and O2, each equal to the
  interpreter.

The trusted test modules contain no measure call, so they test the inline path.

**Speed:**

| | 1 A | 2 B | 3 B | 4 A |
| --- | ---: | ---: | ---: | ---: |
| Uncached req/s | 2,356 | 2,698 | 2,713 | 2,353 |
| Uncached CPU ms per request | 1.794 | 1.560 | 1.558 | 1.802 |
| Cached req/s | 7,521 | 7,818 | 7,731 | 7,703 |
| Cached CPU ms per request | 0.537 | 0.519 | 0.529 | 0.539 |

- **Uncached: 15% more requests a second, and 13% less CPU per request.** It is
  the largest single step since typed rows (report 305).
- **Cached:** about 2%, near the run-to-run spread.

## Step 2: most text skips work it never needed (Flow)

`rich-html.text-ok-for?` runs on every text run of every message. Three
changes give the same answers with less work:
- **Blank test.** It computed "blank", three `string.replace` calls, for every
  run, but only text directly in table structure uses it. `&&` short-circuits
  (CURRENT-SYNTAX), so the test now runs only there.
- **Lower-casing.** `"www."` contains a `.`, so text without a `.` skips the
  lower-cased copy made to look for it.
- **E-mail check.** `rich-html.email-like?` read every byte in Flow looking
  for `@`. Text without an `@` is now answered by one `string.contains`.

Attached tests cover the new branches.

**Speed** (ABBA against step 1):

| | 1 A | 2 B | 3 B | 4 A |
| --- | ---: | ---: | ---: | ---: |
| Uncached req/s | 2,787 | 2,971 | 2,957 | 2,802 |
| Uncached CPU ms per request | 1.557 | 1.454 | 1.471 | 1.559 |
| Cached req/s | 7,857 | 7,999 | 7,854 | 7,882 |
| Cached CPU ms per request | 0.532 | 0.526 | 0.534 | 0.522 |

- **Uncached: 6% more requests a second, and 6% less CPU per request.**
- **Cached:** unchanged.

## Step 3: integers load in one instruction (C)

**What was slow.** `al_owning_read_u32_le` read `base[offset + 1]`,
`base[offset + 2]` and so on with a 32-bit `offset`. `offset + 1` could in
principle wrap, so clang cannot prove the bytes are adjacent. It emitted four
byte loads, three shifts and three ORs for every String length header.
`load_i64` and `store_i64` did the same in a loop over 8 bytes.

**What changed.** The helpers index through a pointer (`p = base + offset;
p[0] | p[1] << 8 …`). Clang now emits one `mov` for each of them; the
disassembly of `al_owning_load_i64` confirms it. The values are the same on
every target, since nothing assumes the byte order.

**Speed** (ABBA against step 2):

| | 1 A | 2 B | 3 B | 4 A |
| --- | ---: | ---: | ---: | ---: |
| Uncached req/s | 2,919 | 3,003 | 3,003 | 2,927 |
| Uncached CPU ms per request | 1.449 | 1.404 | 1.409 | 1.445 |
| Cached req/s | 7,794 | 7,867 | 7,877 | 7,733 |
| Cached CPU ms per request | 0.524 | 0.494 | 0.517 | 0.530 |

- **Uncached: 3% more requests a second, and 3% less CPU per request.**
- **Cached:** 1.4% more requests a second, and 4% less CPU per request.

## Step 4: escaping scans 8 bytes at a time (C)

**What was slow.** Escaping a String hole in an `html"…"` template, or calling
`string.html-escape`, tested one byte at a time for `& ' < > "`. It did so
twice per hole: once in the plan, to count the extra bytes, and once in the
write.

**What changed.**
- **The 8-byte test.** XOR the 8 bytes with each special repeated, then apply
  the standard zero-byte test, `(x − 0x01…) & ~x & 0x80…`. The result is
  nonzero exactly when some byte is a special.
- **Clean words are skipped.** A word with no special moves the scan on 8
  bytes. A word with one is read byte by byte, as before.
- **One copy of the code.** Both plans share one counter, and both writes
  share one writer.

**Checks.**
- **Against the old byte loop:** it matched on 361,378 inputs, at -O2 and under
  ASan and UBSan. The inputs were:
  - every byte value at every position of strings of 0–24 bytes, on two
    backgrounds;
  - every pair of specials in 17 bytes;
  - runs of specials;
  - 200,000 random strings of up to 600 bytes, weighted toward specials and
    nearby bytes.
- **The Layouts suite** now escapes each special at the start, middle and end
  of texts of 1–25 bytes, plus runs of specials and multi-byte text. It covers
  both escape paths, the template hole and `string.html-escape`, and compares
  native O0 and trusted O2 with the interpreter: 628 assertions, up from 105.

**Speed** (ABBA against step 3):

| | 1 A | 2 B | 3 B | 4 A |
| --- | ---: | ---: | ---: | ---: |
| Uncached req/s | 2,999 | 3,072 | 3,070 | 2,981 |
| Uncached CPU ms per request | 1.407 | 1.382 | 1.369 | 1.412 |
| Cached req/s | 7,829 | 8,006 | 7,685 | 7,827 |
| Cached CPU ms per request | 0.526 | 0.516 | 0.519 | 0.525 |

- **Uncached: 3% more requests a second, and 2% less CPU per request.**
- **Cached:** unchanged, within the spread.

## Together

| Step | Kind | Uncached | Cached |
| --- | --- | ---: | ---: |
| 1 | list walks size elements inline (compiler) | **+15%** | +2% |
| 2 | text checks skip unneeded work (Flow stdlib) | **+6%** | 0 |
| 3 | one-instruction integer loads (C runtime) | +3% | +1% |
| 4 | 8-byte escape scan (C runtime) | +3% | 0 |

- **Uncached:** the steps compound to about **+28%**. The first A and the last
  B are from different runs: 2,354 to 3,071 req/s, and CPU per request from
  1.80 to 1.38 ms.
- **Cached:** about +3%.

**The profile now** (uncached, same protocol as before):
- `al_owning_scan_value` is 7.9% (it was 13.7%) and `scan_text` 3.5% (7.0%).
- `al_owning_measure_value` has left the top of the list.
- Template writing is 3.2% (5.4%).
- SQLite is now the largest block, at 18.5% inclusive.

## Considered and not done

- **Writing template literals straight into the HTTP response.** The copies
  this would remove add up to about 2% of CPU at most:
  - `copy_range` under `http-response` and `show-room`, about 1.5%;
  - `memmove`, about 0.5%;
  - some of the `List<Html>` copies.

  Html would stop being a value and become a sink that nested templates and
  `whole-room-page`'s list write into. That is a design change, not a
  low-risk one, so it is left for you to decide.
- **Caching avatar tokens per page.** `campfire.server.avatar-token` is 2.2%
  (HMAC from libcrypto, then Base64 and hex). `message-page` makes a token for
  each run of messages by the same creator. Boosts and mentions make their own
  tokens. One token per distinct user per page would save part of the 2.2%.
  It is a Campfire change and the same answers, but it is not a byte loop, so
  it is listed here.
- **`rich-html.space?` as `inline fn`.** It is a one-line predicate. LLVM
  already inlines its body; marking it would remove the frame release.
  Not measured.
