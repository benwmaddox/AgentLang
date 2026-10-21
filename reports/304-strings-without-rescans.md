# 304 — Item 6: strings without re-scans

Item 6 is about writing templates and strings straight to the response, with
better string concatenation. The profile taken for item 1 (report 301) showed
where string handling actually costs on the uncached room page:
- the runtime re-scans and re-validates values it built itself, about 39% of
  flat CPU (`scan_text`, `scan_value`, `validate_type_descriptor`,
  `scan_readable`);
- concatenation and copying take about 15%.

So the item runs in three steps. Each is measured on its own: one commit, an
ABBA run on both columns, and a profile.
- **(a)** Fast equality for String, Bytes and refined Strings.
- **(b)** Trusted measuring: a value's size, read without re-validating it.
- **(c)** Concatenation and output, designed from the profile taken after (a)
  and (b).

**Safety limit for all three:** the fast paths apply only to values the
generated code built, in trusted builds. Bytes from the host (database rows,
cache hits, the request) are still validated where they enter. The diagnostic
profile keeps every check.

## (a) Fast String equality

**Target:** `al_owning_equal_typed`, 14.4% of uncached CPU, inclusive.
`rich-html.allowed-tag?` and `rich-html.tag` compare tag names with `==`. Each
comparison validated the type graph, then scanned both values in full, then
compared their bytes.

**Change:**
- In trusted builds, `equals` on two Strings, two Bytes or two of a refined
  String type calls `al_owning_string_equal`. It compares the two byte counts,
  then the bytes, 8 at a time; the payloads are 8-byte aligned.
- Diagnostic builds keep `al_owning_equal_typed`. The generated modules show
  each profile using its own path.

**Checks:**
- NativeParity grows to 5,003 assertions. 225 pairs of Strings are compared
  as Strings, as Bytes and as a refined type, at O0, O2 and trusted O2. They
  include lengths 7, 8, 9 and 16 around the 8-byte word, equal prefixes,
  combining marks, emoji, and 99 or 100 bytes.
- All 17 suites pass, and all 22 sampled responses match the base build.

**Speed** (ABBA against the item-4 build; protocol of report 285, 4 vCPUs):

| | 1 A (item 4) | 2 B (6a) | 3 B (6a) | 4 A (item 4) |
| --- | ---: | ---: | ---: | ---: |
| Uncached req/s | 1,208 | 1,407 | 1,418 | 1,202 |
| Uncached CPU ms per request | 3.32 | 2.85 | 2.83 | 3.32 |
| Cached req/s | 5,699 | 5,735 | 5,729 | 5,724 |
| Cached CPU ms per request | 0.675 | 0.679 | 0.680 | 0.682 |

- **Uncached: 17% more requests a second, and 15% less CPU per request.**
- Cached: no change, because the cached room page serves message bodies from
  the worker store.
- **Profile after (a):** `equal_typed` fell from 14.4% to 0.8% inclusive, and
  `string_equal` costs 1.1%. `rich-html.allowed-tag?` fell from 11.2% to
  6.6%; its 52-entry list is still rebuilt on every call.
- **Build:** 39.2 s.

## (b) Trusted measuring

**Target:** the scans under list iteration, `list.get` and Option/Result case
access in generated code:
- `al_owning_measure_value`, 14.6% inclusive;
- `al_owning_locate_sum_case`, 10.1%;
- `al_owning_locate_field`, 3.9%.

Their flat costs were mostly `scan_text` (11.2% self),
`validate_type_descriptor` (6.7% self) and `scan_readable`. Report 270 had
already skipped the type-graph check for values verified code built, but each
node was still re-checked.

**Change:** C only, in trusted builds, for internal views (values the generated
code built). It extends report 270's rule one level down:
- `validate_type_descriptor` returns the compiler-emitted descriptor without
  re-checking it.
- `scan_readable` checks bounds only, and skips the initialization bitmap:
  generated code initializes every byte it builds.
- `scan_text` takes a String's or Bytes value's size from its header, checked
  against its owner. It skips the reserved-byte and padding loops, which are
  zero by construction. UTF-8 was already checked only for external views.

External views keep the full walk: host input, cache hits and anything
`measure_external_value` reads. So do diagnostic builds.

**The differential test** (NativeParity, now 6,590 assertions). 24 seeded
random `List<Shelf>` values are generated:
- a `Shelf` holds a String, a `List<Note>` and an `Option<Note>`;
- a `Note` holds a String, a `List<String>`, an `Option<String>`, a
  `Result<Int, String>` and a Bool;
- Strings are 0 to 20 characters, mixing ASCII, `é`, `🚀`, `日`, space, `<`
  and `&`.

Three programs walk those values: nested loops, `list.get` twice, and matches
on every Option and Result. The interpreter must match an F# reference, and
diagnostic O2 (the full scanner) and trusted O2 (the fast path) must both
match the interpreter. The test passed on the runtime before this change, and
passes after it, with the trusted artifacts confirmed to carry the new C.

**Speed** (ABBA against the 6a build):

| | 1 A (6a) | 2 B (6b) | 3 B (6b) | 4 A (6a) |
| --- | ---: | ---: | ---: | ---: |
| Uncached req/s | 1,411 | 1,660 | 1,651 | 1,416 |
| Uncached CPU ms per request | 2.84 | 2.42 | 2.42 | 2.84 |
| Cached req/s | 5,804 | 6,154 | 6,074 | 5,772 |
| Cached CPU ms per request | 0.676 | 0.631 | 0.634 | 0.679 |

- **Uncached: 17% more requests a second, and 15% less CPU per request.**
- **Cached: 5% more requests a second, and 7% less CPU.** The cached page
  still loops over its messages, users and Options outside the store. Hits from
  the store are external bytes, so they are still validated.
- Output: all 22 sampled responses match. Build: 39.0 s.
- **Together, (a) and (b)** take the uncached room page from 1,205 to 1,655
  req/s (+37%), at 2.42 ms of CPU per request instead of 3.32.

  Report 285 measured the Rust port at 1,554 req/s under the same protocol,
  but in another session, so that comparison is not made here.

## (c) Concatenation and output, from the profile after (b)

**The profile after (b)** (uncached; inclusive unless marked self):

| Cost | Share | What it is |
| --- | ---: | --- |
| `al_owning_copy_range` | 6.1% | over half in `allowed-tag?` and `emoji.ranges`, which rebuild literal lists on every call; the final page copy (`http-response`, `show-room`) is about 1.2% |
| `al_owning_loop_relocate` | 5.7% | values carried round loops (`rich-html.tag`, `rich-html.scan`, `message-row`), moved every step to reclaim the arena |
| `al_owning_copy_constant` | 3.9% | String literals copied into the arena (`allowed-tag?`, `text-ok-for?`) |
| template write and plan | 6.1% | writing the page's own bytes (mostly `rails-message-shown`) |
| concat and replace | 5.6% | |
| host database operations | 9.3% | SQLite 8.7%; rows are external bytes, still validated |

### Step 1: `rich-html.tag` reads names and values as ranges

**Before:** the tag reader built names and values one character at a time:
`name = string.concat(name, char)`, where `char` was `string.slice(text, i, 1)`.
- Each step allocated a one-character String and copied the whole name so far.
- `string.slice` uses byte offsets and returns `none` off a character boundary,
  so each byte of a multi-byte character became `""`. Multi-byte text was
  silently dropped from tag names and attribute values.

**Change:** the reader now records start and end indices and takes one slice
per name or value. A value with a character reference adds a decoded prefix
before the ref.

**Two bugs fixed in the same function:**
- Multi-byte text is now kept. `title="é🚀"` used to read as `""`.
- **The loop's step budget.** It was `length + 1`, but a state change re-reads
  a byte (`advance = 0`) up to twice more. So a tag with several attributes
  could stop before its last one: `<p a b=1 c>` lost `c`. The new attached test
  caught that the old reader had the same bug. The budget is now
  `3 × length + 3`.

**This is a stdlib rewrite, not a language change.** It shows the cost the
language imposes on character-at-a-time building, but doesn't remove it.

**Checks:**
- Two new attached tests: multi-byte values with an unquoted `&amp;`, and
  `<p a b=1 c>`. All 43 rich-html tests pass.
- The byte comparison now covers every room in the fixture. That adds open,
  closed and direct rooms, and room 654632876 (sounds, embeds, mentions,
  attachments, emoji; 356,704 bytes, Rails' bytes in report 285), for 33
  responses. All 33 match the base build, so Campfire's own messages had
  neither multi-byte attribute values nor tags long enough to hit the budget.
- All 17 suites pass.

**Speed** (ABBA against the 6b build):

| | 1 A (6b) | 2 B (6c-1) | 3 B (6c-1) | 4 A (6b) |
| --- | ---: | ---: | ---: | ---: |
| Uncached req/s | 1,678 | 1,722 | 1,710 | 1,669 |
| Uncached CPU ms per request | 2.43 | 2.36 | 2.39 | 2.42 |
| Cached req/s | 6,264 | 6,248 | 6,285 | 6,228 |
| Cached CPU ms per request | 0.628 | 0.628 | 0.626 | 0.630 |

- **Uncached: about 2.5% faster.** Cached: no change. Build: 39.6 s.
- **What this bounds.** This was the page's character-at-a-time building, so
  2–3% is roughly what any concatenation optimization could save here. An
  in-place append in the runtime would also need a proof that the operand is
  dead. On this page that isn't worth the risk.

### Step 2: the pattern, counted

`FlowLint.appendsInLoops` (`FLOW_APPEND_IN_LOOP`) reports a loop that rebuilds
a value from itself. That covers `v = string.concat(…v…)`,
`list.append(…v…)`, `list.concat(…v…)` and `string.join([…v…])`, appended or
prepended. Each step copies all of `v`, so the loop copies O(n²) bytes.
- Three Flow.Lint tests cover it: inside an `if` in a loop, outside a loop,
  and a value rebuilt without itself.
- **Over Campfire and the stdlib it finds 80 sites.** Most are short lists of
  ids or headers. The ones in rich-text and page code (`link`,
  `text-between`, `decode-text`, `write-messages`) are where a builder type
  would matter.

### Step 3: literal lists prepared at compile time

This follows the user's direction: big operations should be bulk copies or
references to compile-time constants, prepared before run time and only
once.

- **Before:** `["a", "abbr", …]` in a function body, 52 Strings in
  `allowed-tag?`, was built on every call:
  1. one String copy per element;
  2. a list built by copying each element again;
  3. a measure of each element on every loop over it.
- **Change, in trusted builds:** when `ListOf(T, n)` directly follows n String
  or Int constants, the compiler encodes the whole list once, in the runtime's
  layout, as read-only data:
  - the image is the u32 count and 4 zero bytes, then each element at its
    8-byte-aligned extent;
  - its payload and extent are worked out at compile time;
  - identical lists share one image.

  The use site is then one `copy_constant` of the finished image. The result is
  an ordinary fresh value in the arena, so nothing can write into the shared
  image. That includes the in-place append, which only fires for lists at the
  top of the arena.
  - A list feeding `string.join` keeps its existing fusion.
  - Diagnostic builds keep the element-by-element build, so the parity tests
    compare the two paths.
- **Campfire's module has 19 list images.**
- **Bulk copies.** `al_owning_bytes_copy` already moved 16 bytes at a time, so
  the copies themselves were already memcpy-style. The cost was their number.

**Checks:**
- NativeParity, 6,780 assertions. Four new words run in all three
  configurations:
  - a 15-entry String membership test with `é` and `🚀`, called twice per
    case;
  - an Int literal sum near ±9.2×10¹⁸;
  - a literal list appended to twice, called repeatedly. If the shared image
    were ever written, the next call would differ.
  - a literal list joined with `string.join`.

  The trusted module carries the image; the diagnostic module doesn't.
- All 33 responses match. Build: 38.9 s. All 17 suites pass.

**Speed** (ABBA against 6c-1):

| | 1 A (6c-1) | 2 B (this) | 3 B (this) | 4 A (6c-1) |
| --- | ---: | ---: | ---: | ---: |
| Uncached req/s | 1,710 | 1,889 | 1,897 | 1,716 |
| Uncached CPU ms per request | 2.37 | 2.14 | 2.15 | 2.39 |
| Cached req/s | 6,235 | 6,269 | 6,338 | 6,253 |
| Cached CPU ms per request | 0.628 | 0.635 | 0.625 | 0.609 |

- **Uncached: 10.6% more requests a second, and 10% less CPU per request.**
  That is twice the 4–5% estimated from `copy_constant` alone: the element
  measures and the list build went too.
- Cached: no change.
- **Item 6 so far:** the uncached room page has gone from 1,205 req/s (item 4)
  to about 1,893 req/s (**+57%**), and CPU per request from 3.32 ms to 2.14 ms.
  The cached page has gone from about 5,700 to about 6,270 req/s (+10%).
  Memory is unchanged, at 15 MB uncached and 47 MB cached.

### Not done in (c), for the user

- **A builder or rope type.** This is the general fix for the 80 sites, and the
  final report's recommendation ("plan a builder or rope type early"). It is a
  language design decision.
- **A shared constant region.** Today each call still copies the prepared
  image into its arena; one memcpy, but a copy. Referencing a constant in
  place would need borrowed descriptors that drops never rewind, set up in each
  worker's protected prefix. It is a larger change, for whatever remains after
  step 3.
- **Writing the page straight to the response.** The final copy of the page is
  about 1.2% of CPU on this page, so on today's numbers this headline idea is
  a small win.
