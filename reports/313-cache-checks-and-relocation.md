# 313 — The cache's own checks, and loop relocation

Two changes, each byte-identical on the 45 sampled responses:
1. **Trusted builds stop re-checking the program's own layout when they admit
   an external value.**
2. **A literal byte set no longer costs an allocation**, which had switched off
   the loop-relocation skip.

## 1. External values: the layout walk goes, the value checks stay

**What the cached page paid for.** Report 312's profile put about 11% of the
cached page's CPU in `worker-cache.find`, almost all in value checks:
- **The lookup makes many host calls.** It makes up to 8 index probes, each
  with `store.ints` (an `Option<List<Int>>`) and `store.matches`.
- **Every result is admitted as an external value.** It is checked like
  anything a host hands in, through `al_owning_measure_external_value`.
- **That check walked the module's whole type layout first.** The walk starts
  by clearing two per-type arrays one byte at a time, volatile on purpose so
  the freestanding runtime needs no `memset`. Then every step of the scan
  re-checked its type descriptor.

**Why that was redundant.** The walk and the descriptor checks read only the
layout, never the value's bytes; the view is used only to report a failure.
In a trusted build, the layout is a constant the compiler emitted. Trusted
builds already skipped both for values the program built itself (reports 270
and 304). Now they also skip them for external values.

**What still runs on an external value's bytes:**
- bounds against the source;
- list counts against the bytes available;
- Option, Result and enum tags;
- Bool values;
- zero reserved and padding bytes;
- string lengths;
- UTF-8.

Diagnostic builds, and runtimes built without `AL_OWNING_TRUSTED_GENERATED`,
keep the walk.

**Checks.** `external_values_test.c` feeds 27 values through
`al_owning_measure_external_value`, with its own small layout (Int, Bool,
String, enum, `List<Int>`, `List<String>`, `Option<String>`, a record):
- **Valid values:** each kind, including multi-byte UTF-8.
- **Malformed values:**
  - a Bool of 2, an enum ordinal past its cases, an Option tag of 2;
  - nonzero reserved and padding bytes;
  - a string length past the end, a cut UTF-8 sequence, a surrogate, an
    overlong encoding;
  - a list count too large, or huge;
  - a bad list element;
  - truncated records.
- **Two builds.** It is built with the trusted runtime and with the
  layout-walking one, and both must give the same answers. The walking build
  also checks the test's hand-written layout. Both run in
  `Test-NativeRuntime.ps1` (with UBSan for the trusted one).

**Speed** (ABBA against report 312's final build):

| | 1 A | 2 B | 3 B | 4 A |
| --- | ---: | ---: | ---: | ---: |
| Uncached req/s | 3,427 | 3,514 | 3,366 | 3,327 |
| Uncached CPU ms per request | 1.288 | 1.226 | 1.285 | 1.297 |
| Cached req/s | 9,791 | 10,972 | 11,046 | 9,568 |
| Cached CPU ms per request | 0.448 | 0.393 | 0.391 | 0.446 |

- **Cached: 13.7% more requests a second, and 12% less CPU per request.**
- **Uncached: about 2% more requests a second, and 3% less CPU per request.**
  The database rows and other host results are external values too.

## 2. Loop relocation, and a regression report 312 caused

**What relocation is.** Values live in a per-request arena that only grows
until it is released. A `for` loop releases at each back edge (the jump back
to the top), but the loop's carried `var`s may sit in the region being
released. So `al_owning_loop_relocate`:
- copies every carried value above the loop's mark up past the cursor;
- moves them back down as one block, onto the mark;
- releases everything above them.

The cost is about twice the bytes of every carried value above the mark, on
every iteration that allocated.

**The skip.** Report 308 records where each iteration starts allocating. An
iteration that allocated nothing moved nothing, so its relocation is skipped.

**Timing.** Shares of the uncached page's CPU, server samples only, in
profiling builds:

| When | Share | Per request | Mostly in |
| --- | ---: | ---: | --- |
| before report 308 | 3.96% | ~74 µs | `rich-html.tag` |
| after report 308's per-iteration mark | 1.5% | ~28 µs | `whole-room-page` 0.6, `rich-html.tag` 0.4 |
| after report 312 | 3.2% | ~42 µs | **`rich-html.tag` 1.3**, `whole-room-page` 0.9 |
| after this change | 2.6% | ~33 µs | `whole-room-page` 1.0, `rich-html.tag` 0.7 |

**The regression.** Report 312 made the tokenizer jump with
`string.next-byte-in(text, "…", i)`. Each call still copied its literal set
into the arena, although the call takes the set's byte table as constants. So
the scanning iterations, which allocated nothing before, now allocated about
48 bytes each. The skip stopped firing, and `rich-html.tag` copied its carried
values (the `attributes` list, `value-prefix`, `attr-value`) up and back down
far more often.

**The fix.** A pre-pass (`setOnlyLiterals`) simulates the IR stack. It marks
String literals that are pushed and consumed directly as a next-byte call's
`set`: never through a local, never duplicated, never used again. Trusted
builds push those as a descriptor with no bytes. All 14 such call sites in
Campfire lose their copy. Diagnostic builds keep it.

**Speed** (ABBA against step 1):

| | 1 A | 2 B | 3 B | 4 A |
| --- | ---: | ---: | ---: | ---: |
| Uncached req/s | 3,587 | 3,642 | 3,657 | 3,552 |
| Uncached CPU ms per request | 1.236 | 1.217 | 1.213 | 1.244 |
| Cached req/s | 10,953 | 11,319 | 10,969 | 11,052 |
| Cached CPU ms per request | 0.394 | 0.390 | 0.395 | 0.392 |

- **Uncached: 2.2% more requests a second, and 2% less CPU per request.**
- **Cached:** unchanged; its loops do not scan text.

## What is left in relocation

Uncached, 2.6% of CPU, about 33 µs per request:
- **`whole-room-page`, 1.0%.** Its loop appends 40 rendered messages to a
  `List<Html>`. Each append reallocates the list, so each iteration moves it.
  This is the case report 308's builder type would remove, worth about 1%
  here.
- **`rich-html.tag`, 0.7%.** These are the iterations that really allocate: an
  attribute's name and value slices and the list append. They are down from
  1.3%, but still above the 0.4% they were before report 312. The remaining
  difference is in the tokenizer's restructured branches and is not traced
  further. A tokenizer that returned offsets instead of Strings would remove
  most of it (report 312, "Not done").
- **Small loops**, 0.2% or less each: `id-list`, `plain-text`, `message-page`.

Cached, 0.9%, almost all in `cache.key`. It builds a fragment key with
`key = string.concat(key, part)` in a loop, so the growing key is moved on
each iteration. A single `string.join` of the parts and separators would make
one string, with no carried value.

## Where the time goes now

| | Uncached (1.28 ms CPU) | Cached (0.43 ms CPU) |
| --- | ---: | ---: |
| value checks | 18.1% | 15.6% (was 26.3%) |
| compiled Flow code | 15.2% | 9.2% |
| SQLite | 11.8% | **20.4%**, now the largest |
| copying values | 10.6% | 3.9% |
| arena bookkeeping | 8.8% | 6.0% |
| HTML templates | 8.3% | 1.2% |
| string operations | 7.7% | 6.5% |
| stdlib dispatch | 7.5% | 4.9% |
| kernel (sockets) | 6.8% | 18.8% |

`worker-cache.find` is now 9.3% of the cached page, down from about 18%. The
rest of the cached page is SQLite's page-keys query (20%), the kernel sending
the page (19%), and the cache's key building (`cache.key` 7%,
`fragment-key` 6%).

